using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using PfExplorer.Models;

namespace PfExplorer;

// Polls GET /api/listings on the same cadence the website's own auto-refresh
// uses and keeps a live list of listings matching the configured job/ilvl/DC
// filters, browsable straight from the plugin window instead of a browser
// tab. Independent of Configuration.Enabled (capture/upload): you might
// want alerts without contributing capture data, or vice versa.
public sealed class AlertPoller : IDisposable
{
    // Plays the default Windows notification chime — avoids pulling in the
    // System.Windows.Extensions package just for System.Media.SystemSounds
    // on this SDK target.
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool MessageBeep(uint uType);
    private const uint MB_ICONASTERISK = 0x00000040;

    private const int PollIntervalMs = 30_000;
    private const int MaxAnnouncementsPerPoll = 3;

    // +/-15% randomized into every scheduled poll (see ScheduleNextPoll) so
    // that many clients launched around the same moment (e.g. everyone
    // reconnecting after a game patch) don't stay locked in step polling
    // the server in the exact same instant every 30s forever — spreads the
    // load out instead of a repeating thundering herd.
    private const double JitterFraction = 0.15;
    private static readonly Random JitterRandom = new();

    private readonly Configuration _config;
    private readonly IPluginLog _log;
    private readonly IChatGui _chatGui;
    private readonly IToastGui _toastGui;
    // The server gzips GET /api/listings (server/src/index.ts's compression
    // middleware) but only for requests that actually advertise
    // Accept-Encoding — a bare HttpClient never sends that header or
    // decompresses a response on its own, so without this the middleware
    // never engages and every poll pulls the full uncompressed body
    // (~300KB-1.2MB depending on how narrow the configured filters are).
    private readonly HttpClient _http = new(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
        { Timeout = TimeSpan.FromSeconds(10) };
    private readonly Timer _timer;

    // Keyed by ListingId (the game's own stable Party Finder listing id),
    // not the server row's numeric Id — that numeric id is NOT stable for
    // the same real-world listing: server/src/routes/listings.ts's
    // dedupeListings collapses a plugin-sourced row and an xivpf-sourced
    // row of the same listing_id onto whichever one it currently prefers
    // (the plugin's, once one exists), and those are two different DB
    // rows with two different numeric ids. The instant a plugin capture
    // for a listing xivpf had already surfaced shows up server-side (e.g.
    // from opening PF yourself), the numeric id GET /api/listings returns
    // for that same real listing flips — which, tracked by numeric Id,
    // looks exactly like "the old one vanished, a new one appeared" and
    // fired a bogus "changed" then "new" pair for one continuous listing.
    // ListingId doesn't have this problem — it's the real game id, shared
    // by both rows.
    //
    // Null until the first poll completes, so enabling alerts mid-session
    // doesn't flag every match that was already up as "new".
    private HashSet<string>? _previousMatchingIds;

    // Every listing id that's ever been seen in a matching poll, kept
    // forever (until ResetBaseline) — unlike _previousMatchingIds, entries
    // are never removed just because a listing drops out of one poll's
    // results. Without this, a listing that ages past the freshness
    // window then gets recaptured looks like a brand new match to
    // _previousMatchingIds and fires a duplicate "new match" notification
    // even though nothing actually changed in-game. This set is the
    // source of truth for "have we already announced this one", separate
    // from NewMatchIds (which stays poll-to-poll and only drives the
    // list's transient "new" highlight).
    private readonly HashSet<string> _announcedMatchIds = new();

    // ListingId -> (when this suppression expires, which bucket/DC it was
    // removed from). Populated by PruneMissing when PfScanTracker confirms
    // (from your own organic PF browsing) that a complete, unfilled category
    // page no longer contains a listing — without this, the very next poll
    // (its own ~30s timer, independent of that) would just silently bring it
    // right back, since the server's own "active" definition lags well
    // behind what the game itself just told us: a row only drops out of
    // activeOnly results once its updated_at ages past
    // LISTING_FRESHNESS_MINUTES (5min by default), and a listing double-
    // sourced via xivpfSync.ts's independent re-scrape sits under a
    // completely separate DB row that can stay "active" even longer if
    // xivpf.com's own data hasn't caught up yet. Cleared the moment a scan
    // sees the listing again (see RefreshFromScan), so a false prune
    // self-heals within one scan cycle instead of being stuck hidden for
    // the full grace window below.
    //
    // Bucket/DataCenter (rather than just the timestamp) exist so
    // PruneMissing can reconfirm an entry here against a LATER scan of the
    // same category — a single ExpireAsync report can fail to actually take
    // server-side (dropped mid-flight, or the two-rows-per-real-listing
    // situation above, where a report against one row doesn't necessarily
    // clear the other) — so every time a fresh scan of the same bucket/DC
    // still doesn't see it either, PruneMissing resends the report and
    // refreshes this entry's expiry, instead of firing exactly once and
    // hoping.
    private readonly record struct LocalRemoval(DateTime ExpiresAt, string Bucket, string DataCenter);
    private readonly Dictionary<string, LocalRemoval> _locallyRemovedListingIds = new();
    private static readonly TimeSpan LocalRemovalGrace = TimeSpan.FromMinutes(10);

    // listing id -> slotsFilled as of the last poll, for detecting a party
    // size change (5/8 -> 6/8) on a listing that was already matching.
    // Tracked for every match, not just filtered/announced ones, so the
    // diff is always correct regardless of what AlertNotifyOnPartyChange is
    // set to at any given moment.
    private Dictionary<string, int>? _previousSlotsFilled;

    // Consecutive polls a previously-matching listing has been absent from
    // the server's response — PollAsync's own "gone" path (low-confidence:
    // the server just stopped returning it, not ground truth) requires this
    // to cross MissingPollThreshold, and the listing's own freshness to have
    // aged into red, before announcing. Absorbs the case where a listing
    // drops out for exactly one poll right as it crosses the server's own
    // LISTING_FRESHNESS_MINUTES cutoff (which lines up almost exactly with
    // MatchFreshness's red threshold) but is still real — RefreshFromScan
    // (fed by manual PF browsing, see Plugin.OnReceiveListing) resets an
    // id's streak back to 0 the moment anything actually re-observes it.
    private readonly Dictionary<string, int> _missingPollStreak = new();
    private const int MissingPollThreshold = 2;

    // Guards against the manual refresh button and the timer firing a poll
    // at the same time and racing each other.
    private volatile bool _isPolling;
    public bool CanManualRefresh => _config.AlertEnabled && !_isPolling;

    public IReadOnlyList<PfListingSearchResult> Matches { get; private set; } = Array.Empty<PfListingSearchResult>();

    // Listing IDs that weren't in the previous poll's matching set — the
    // window highlights these instead of firing an individual toast per one.
    public IReadOnlySet<string> NewMatchIds { get; private set; } = new HashSet<string>();

    public DateTime? LastPollAt { get; private set; }

    // Approximate — the underlying Timer fires on its own fixed
    // PollIntervalMs cadence regardless of manual RequestPoll() calls in
    // between, so this is "assuming nothing pokes it early" rather than an
    // authoritative next-fire time. Good enough for the "Refresh in Xs"
    // display next to the manual Refresh button.
    public DateTime? NextPollAt => LastPollAt?.AddMilliseconds(PollIntervalMs);
    public string? LastError { get; private set; }

    // A fixed-size rotating pool of Dalamud chat link handlers, each backing
    // one clickable announcement — Dalamud's AddChatLinkHandler needs a
    // stable commandId registered up front, so this pre-registers a fixed
    // set rather than one per message (which would leak a handler per
    // notification for the rest of the session). Round-robins through the
    // pool (see NextLinkPayload) — old lines beyond the pool size fall back
    // to whatever listing that slot was last reused for if clicked, which
    // in practice only matters for scrollback older than ~ClickableLinkPoolSize
    // clickable announcements back, an acceptable tradeoff for not leaking.
    private const int ClickableLinkPoolSize = 32;
    private readonly DalamudLinkPayload[] _linkPayloads = new DalamudLinkPayload[ClickableLinkPoolSize];
    private readonly PfListingSearchResult?[] _linkTargets = new PfListingSearchResult?[ClickableLinkPoolSize];
    private int _nextLinkSlot;

    public AlertPoller(Configuration config, IPluginLog log, IChatGui chatGui, IToastGui toastGui)
    {
        _config = config;
        _log = log;
        _chatGui = chatGui;
        _toastGui = toastGui;
        // One-shot timer, rescheduled after every tick (see OnTimerTick)
        // rather than a fixed period — that's what lets each cycle get a
        // fresh jittered delay instead of locking onto one wall-clock
        // cadence forever. dueTime: 0 — poll immediately on load instead of
        // waiting a full PollIntervalMs for the first result (PollAsync
        // itself no-ops if AlertEnabled is still off, so this is harmless
        // either way).
        _timer = new Timer(_ => OnTimerTick(), null, 0, Timeout.Infinite);

        for (var i = 0; i < ClickableLinkPoolSize; i++)
        {
            var slot = i;
            _linkPayloads[i] = _chatGui.AddChatLinkHandler((uint)slot, (_, _) =>
            {
                if (_linkTargets[slot] is { } target)
                    PfListingOpener.Open(target);
            });
        }
    }

    // Claims the next slot in the rotating pool for `listing` and returns
    // its payload — call once per clickable announcement, right before
    // building the message that embeds it.
    private DalamudLinkPayload ClaimLinkPayload(PfListingSearchResult listing)
    {
        var slot = _nextLinkSlot;
        _nextLinkSlot = (_nextLinkSlot + 1) % ClickableLinkPoolSize;
        _linkTargets[slot] = listing;
        return _linkPayloads[slot];
    }

    private void OnTimerTick()
    {
        _ = PollAsync();
        _timer.Change(NextIntervalWithJitter(), Timeout.InfiniteTimeSpan);
    }

    private static TimeSpan NextIntervalWithJitter()
    {
        var jitterMs = (JitterRandom.NextDouble() * 2 - 1) * PollIntervalMs * JitterFraction;
        return TimeSpan.FromMilliseconds(PollIntervalMs + jitterMs);
    }

    // Config changes (alerts just turned on, filters changed) should apply
    // on the very next tick instead of waiting up to PollIntervalMs — and
    // re-baseline so a filter change doesn't flag matches that were already
    // sitting there before the change as "new" or "changed".
    public void ResetBaseline()
    {
        _previousMatchingIds = null;
        _previousSlotsFilled = null;
        _announcedMatchIds.Clear();
        _missingPollStreak.Clear();
    }

    // Fire-and-forget: called from the window's manual refresh button so a
    // click doesn't block ImGui's draw loop waiting on the HTTP round trip.
    public void RequestPoll()
    {
        if (CanManualRefresh)
            _ = PollAsync();
    }

    // Narrows the request server-side whenever it's safe to (job can't be:
    // the server's job param only accepts one value, but AlertJobs is a
    // "match any of these" list, so passing just one would incorrectly
    // drop listings open to the others). A single selected data center or
    // a max item level bound cuts the response down to only what could
    // ever match, instead of always fetching every active listing across
    // every region and filtering client-side — smaller payload and less
    // DB work per client, and it adds up with more contributors polling.
    private string BuildQueryString()
    {
        var parts = new List<string> { "activeOnly=true" };

        if (_config.AlertExcludeXivpf)
            parts.Add("excludeXivpf=true");

        var narrowed = false;
        if (_config.AlertDataCenters.Count == 1)
        {
            parts.Add($"dataCenter={Uri.EscapeDataString(_config.AlertDataCenters[0])}");
            narrowed = true;
        }

        if (_config.AlertIlvlMax > 0)
        {
            parts.Add($"maxItemLevel={_config.AlertIlvlMax}");
            narrowed = true;
        }

        // 2000 covers the worst case (no filter, every region at once —
        // see the limit comment on searchSchema in routes/listings.ts);
        // once the server's already scoped to one DC and/or an ilvl cap,
        // far fewer rows can possibly come back.
        parts.Add($"limit={(narrowed ? 500 : 2000)}");

        return string.Join("&", parts);
    }

    private async Task PollAsync()
    {
        if (!_config.AlertEnabled || _isPolling)
            return;

        _isPolling = true;
        try
        {
            var url = $"{_config.ServerUrl.TrimEnd('/')}/api/listings?{BuildQueryString()}";
            using var response = await _http.GetAsync(url).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                LastError = $"HTTP {(int)response.StatusCode}";
                _log.Warning($"[PfExplorer] alert poll failed: {LastError}");
                return;
            }

            var body = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            var parsed = await JsonSerializer.DeserializeAsync<PfListingSearchResponse>(body).ConfigureAwait(false);
            var listings = parsed?.Listings ?? new List<PfListingSearchResult>();

            // Drop anything a scan already confirmed gone (see
            // _locallyRemovedListingIds's doc comment) before this poll gets
            // anywhere near rebuilding Matches from it — otherwise a listing
            // PruneMissing just removed would get silently reinstated by
            // this exact poll, since the server can still consider it
            // "active" for several more minutes.
            if (_locallyRemovedListingIds.Count > 0)
            {
                var now = DateTime.UtcNow;
                foreach (var expiredId in _locallyRemovedListingIds
                             .Where(kv => kv.Value.ExpiresAt <= now)
                             .Select(kv => kv.Key)
                             .ToList())
                    _locallyRemovedListingIds.Remove(expiredId);

                if (_locallyRemovedListingIds.Count > 0)
                    listings = listings.Where(l => !_locallyRemovedListingIds.ContainsKey(l.ListingId)).ToList();
            }

            // This poll's server snapshot only reflects whatever was last
            // actually uploaded — it doesn't know about RefreshFromScan
            // updates fed by your own organic PF browsing (Plugin.
            // OnReceiveListing), which only ever touch the local copy.
            // Without this, every ~30s poll (running on its own
            // timer, independent of the scan cycle) would stomp straight
            // back over a locally fresher CapturedAt/slot state with this
            // now-stale one. Keep whichever side actually saw the listing
            // more recently.
            // Snapshotted before Matches gets overwritten below — needed to
            // build a removed-listing announcement (duty/recruiter/world),
            // since that data won't exist in this poll's response anymore.
            var previousMatchesById = Matches.ToDictionary(l => l.ListingId);
            var previousByListingId = previousMatchesById;
            foreach (var listing in listings)
            {
                if (!previousByListingId.TryGetValue(listing.ListingId, out var previous))
                    continue;
                if (!DateTime.TryParse(previous.CapturedAt, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var previousCaptured))
                    continue;
                if (!DateTime.TryParse(listing.CapturedAt, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var incomingCaptured))
                    continue;
                if (previousCaptured <= incomingCaptured)
                    continue;

                listing.CapturedAt = previous.CapturedAt;
                listing.Description = previous.Description;
                listing.SlotsAvailable = previous.SlotsAvailable;
                listing.SlotsFilled = previous.SlotsFilled;
                listing.JobsPresent = previous.JobsPresent;
                listing.OpenSlotJobs = previous.OpenSlotJobs;
                listing.Tags = previous.Tags;
            }

            // Server-diff absence is low-confidence (see _missingPollStreak's
            // doc comment) — a previously-matching listing this poll's
            // response doesn't include gets kept around (using its last
            // known data), and never counted as actually gone, until BOTH:
            //   - it's been missing for MissingPollThreshold consecutive
            //     polls (absorbs a single transient/timing-edge absence),
            //     and
            //   - its own freshness has aged into red (worse than yellow) —
            //     a listing still green/yellow was reconfirmed too recently
            //     to plausibly be gone already; a real removal will have had
            //     time to age into red by the time the streak alone would've
            //     confirmed it anyway.
            // Both conditions live in this one retention decision (not a
            // separate later gate on the announcement) so the result row and
            // the "gone" announcement always agree — the row never quietly
            // disappears from Matches while only the chat line is held back.
            // Streak resets to 0 the instant the server response includes it
            // again; freshness resets any time RefreshFromScan re-observes
            // it (background scan or you browsing PF yourself).
            var respondedListingIds = listings.Select(l => l.ListingId).ToHashSet();
            foreach (var previous in Matches)
            {
                if (respondedListingIds.Contains(previous.ListingId))
                {
                    _missingPollStreak.Remove(previous.ListingId);
                    continue;
                }

                var streak = _missingPollStreak.GetValueOrDefault(previous.ListingId) + 1;
                _missingPollStreak[previous.ListingId] = streak;
                var isStale = streak >= MissingPollThreshold && MatchFreshness.Rank(previous.CapturedAt) >= 2;
                if (!isStale)
                    listings.Add(previous);
            }

            var matching = listings.Where(Matches_).ToList();
            var matchingIds = matching.Select(l => l.ListingId).ToHashSet();

            var previousIds = _previousMatchingIds;
            var previousSlots = _previousSlotsFilled;
            NewMatchIds = previousIds == null ? new HashSet<string>() : matchingIds.Where(id => !previousIds.Contains(id)).ToHashSet();
            Matches = matching;
            _previousMatchingIds = matchingIds;
            _previousSlotsFilled = matching.ToDictionary(l => l.ListingId, l => l.SlotsFilled);
            LastPollAt = DateTime.UtcNow;
            LastError = null;

            if (previousIds != null)
            {
                // Job/ilvl/DC (Matches_) already narrowed `matching`, but the
                // category and freshness filters (Configuration.AlertCategory/
                // AlertFreshness — the "Show only"/freshness dropdowns) are
                // display-only in MatchListView and were never applied here,
                // so a notification fired for every newly-matching listing
                // regardless of which tab/freshness you'd picked. Apply both
                // here too before announcing.
                if (_config.AlertNotifyOnNewMatch)
                {
                    // Notification eligibility uses _announcedMatchIds (never
                    // forgets an id once seen) rather than NewMatchIds (which
                    // only compares against last poll) — see the field's doc
                    // comment for why: a listing flapping in/out of the
                    // server's active window shouldn't re-announce.
                    //
                    // A burst of new matches at once (e.g. right after
                    // enabling, or a wave of listings posting together)
                    // shouldn't spam a chat line/toast/sound per listing.
                    var newMatches = matching
                        .Where(l => !_announcedMatchIds.Contains(l.ListingId))
                        .Where(PassesDisplayFilters)
                        .Take(MaxAnnouncementsPerPoll);

                    foreach (var listing in newMatches)
                        AnnounceNewMatch(listing);
                }

                if (_config.AlertNotifyOnPartyChange && previousSlots != null)
                {
                    // Only listings that were already matching (not brand
                    // new this poll) and whose slot count actually differs
                    // from last time we saw them.
                    var changed = matching
                        .Where(l => !NewMatchIds.Contains(l.ListingId))
                        .Where(l => previousSlots.TryGetValue(l.ListingId, out var prevFilled) && prevFilled != l.SlotsFilled)
                        .Where(PassesDisplayFilters)
                        .Take(MaxAnnouncementsPerPoll);

                    foreach (var listing in changed)
                        AnnouncePartyChange(listing, previousSlots[listing.ListingId]);
                }

                if (_config.AlertNotifyOnRemoved)
                {
                    // Ids that were matching last poll and still aren't,
                    // after the retention loop above already gave them
                    // MissingPollThreshold polls' grace and required red
                    // freshness — so anything that lands here has been both
                    // absent a while and not reconfirmed by anyone,
                    // including this client's own local view. Covers a
                    // listing aging out of the server's own active window or
                    // getting expired via the verified-deletion consensus,
                    // for DCs your own browsing can't directly confirm. A
                    // scan-confirmed removal (PruneMissing) announces
                    // separately and immediately — ground truth from the
                    // game itself doesn't need to wait on any of this; by
                    // the time this runs, PruneMissing has already scrubbed
                    // that id out of previousIds too, so there's no
                    // double-announce between the two paths.
                    var removed = new List<PfListingSearchResult>();
                    foreach (var id in previousIds)
                    {
                        if (matchingIds.Contains(id))
                            continue;
                        if (!previousMatchesById.TryGetValue(id, out var listing))
                            continue;
                        if (!PassesDisplayFilters(listing))
                            continue;

                        removed.Add(listing);
                        if (removed.Count >= MaxAnnouncementsPerPoll)
                            break;
                    }

                    foreach (var listing in removed)
                        AnnounceRemoved(listing);
                }
            }

            // Seeded every poll (not just for ones that get announced) so a
            // listing that's currently hidden by the category/freshness
            // display filter doesn't later "become new" the moment it starts
            // passing that filter — see _announcedMatchIds's doc comment.
            // Has to run after the new-match check above, not before: it
            // used to run first, which meant _announcedMatchIds already
            // contained every currently-matching id by the time that check
            // ran, so "not already announced" was never true for anything —
            // no "new" notification ever fired, for any listing, ever.
            foreach (var id in matchingIds)
                _announcedMatchIds.Add(id);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _log.Warning(ex, "[PfExplorer] alert poll failed");
        }
        finally
        {
            _isPolling = false;
        }
    }

    // Shared between PollAsync's new-match/party-change/removed
    // announcement gating and PruneMissing's own removed announcement —
    // the category/freshness "Show only" filters are display-only
    // (MatchListView), never applied to what AlertPoller considers a
    // match in the first place, so every announcement path has to apply
    // them itself.
    private bool PassesDisplayFilters(PfListingSearchResult l) =>
        (string.IsNullOrEmpty(_config.AlertCategory) || MatchCategorizer.CategoryBucket(l) == _config.AlertCategory)
        && (_config.AlertFreshness < 0 || MatchFreshness.Rank(l.CapturedAt) <= _config.AlertFreshness);

    // Called by Plugin.OnReceiveListing for every listing the game shows
    // you (organic PF browsing — a listing this actually saw is
    // trustworthy regardless of how many others came back alongside it) to
    // fold fresh data straight into any Match already tracking that same
    // listing, instead of leaving it to show party size/tags/CapturedAt as
    // they were as of the last ~30s poll. Mutates the matched
    // PfListingSearchResult objects in place (they're reference types with
    // public setters, already sitting in Matches) rather than rebuilding
    // the list. Scoped to `dataCenter` for the same reason as PruneMissing
    // below — organic browsing can only ever see the player's own DC.
    //
    // Also adds any listing this scan saw that ISN'T already tracked but
    // passes the configured job/ilvl/DC filters (Matches_) — otherwise a
    // genuinely new listing you just spotted in-game wouldn't show up here
    // at all until the next ~30s PollAsync round-trip rebuilds Matches from
    // the server, even though this client just proved it exists right now.
    // This DOES touch NewMatchIds/_previousMatchingIds/_previousSlotsFilled/
    // _announcedMatchIds bookkeeping (unlike the in-place refresh above) —
    // gated on _previousMatchingIds already being non-null (i.e. the first
    // real poll has completed) for the same reason PollAsync itself
    // withholds "new match" announcements until then: before that baseline
    // exists there's nothing sensible to compare "new" against, and this
    // method's own bookkeeping updates below assume that baseline is
    // already in a normal steady state.
    public int RefreshFromScan(IReadOnlyList<PfListingDto> freshListings, string dataCenter)
    {
        if (freshListings.Count == 0)
            return 0;

        // A listing the scan just actually saw is definitionally not gone —
        // clear any earlier suppression so it's eligible to reappear on the
        // very next poll instead of staying hidden for the rest of
        // LocalRemovalGrace.
        foreach (var listing in freshListings)
            _locallyRemovedListingIds.Remove(listing.ListingId);

        // Last-write-wins on an (unexpected) duplicate id within the same
        // batch — there's no ordering signal worth preferring one over the
        // other here.
        var freshByListingId = freshListings
            .GroupBy(l => l.ListingId)
            .ToDictionary(g => g.Key, g => g.Last());

        var existingIds = Matches.Select(m => m.ListingId).ToHashSet();

        var updated = 0;
        foreach (var match in Matches)
        {
            if (!string.Equals(match.DataCenter, dataCenter, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!freshByListingId.TryGetValue(match.ListingId, out var fresh))
                continue;

            match.Description = fresh.Description;
            match.SlotsAvailable = fresh.SlotsAvailable;
            match.SlotsFilled = fresh.SlotsFilled;
            match.JobsPresent = fresh.JobsPresent;
            match.OpenSlotJobs = fresh.OpenSlotJobs;
            match.Tags = fresh.Tags;
            match.CapturedAt = fresh.CapturedAt;
            // Actually re-observed, from your own PF browsing — not just
            // "the server still lists it". See _missingPollStreak's doc
            // comment.
            _missingPollStreak.Remove(match.ListingId);
            updated++;
        }

        if (_previousMatchingIds != null)
        {
            // From freshByListingId (already deduplicated by ListingId),
            // not freshListings directly — the same real-world listing
            // appearing twice in one batch would otherwise add two separate
            // Matches entries for it and throw below (_previousSlotsFilled
            // indexer would be fine, but this used to be .Add, which isn't
            // — kept as an indexer now specifically so a future duplicate
            // slipping through some other path fails safe instead of
            // throwing).
            var newlyMatching = freshByListingId.Values
                .Where(l => string.Equals(l.DataCenter, dataCenter, StringComparison.OrdinalIgnoreCase))
                .Where(l => !existingIds.Contains(l.ListingId))
                .Select(ToSearchResult)
                .Where(Matches_)
                .ToList();

            if (newlyMatching.Count > 0)
            {
                Matches = Matches.Concat(newlyMatching).ToList();
                foreach (var listing in newlyMatching)
                {
                    _previousMatchingIds.Add(listing.ListingId);
                    if (_previousSlotsFilled != null)
                        _previousSlotsFilled[listing.ListingId] = listing.SlotsFilled;
                }

                NewMatchIds = NewMatchIds.Concat(newlyMatching.Select(l => l.ListingId)).ToHashSet();

                // Same eligibility/cap/gating PollAsync's own new-match
                // announcement uses (see its own comment) — _announcedMatchIds
                // marked regardless of whether AlertNotifyOnNewMatch is on,
                // same as PollAsync's own unconditional seed loop, so a
                // listing captured here while notifications happened to be
                // off doesn't announce retroactively the moment they're
                // turned back on.
                if (_config.AlertNotifyOnNewMatch)
                {
                    foreach (var listing in newlyMatching
                                 .Where(l => !_announcedMatchIds.Contains(l.ListingId))
                                 .Where(PassesDisplayFilters)
                                 .Take(MaxAnnouncementsPerPoll))
                        AnnounceNewMatch(listing);
                }

                foreach (var listing in newlyMatching)
                    _announcedMatchIds.Add(listing.ListingId);
            }
        }

        return updated;
    }

    // PfListingDto (the shape ReceiveListing events map to) -> the subset
    // of PfListingSearchResult fields (the shape server search results —
    // and Matches — use) a locally-captured listing can actually supply.
    // Id is the SERVER's numeric row id, which a listing captured straight
    // from the game doesn't have yet — only ListingId (the game's own,
    // used for everything that actually matters: matching, pruning,
    // OpenListing) is real here. A hash keeps MatchListView's per-row ImGui
    // widget ids (the only thing Id is actually used for) collision-free
    // without needing a real one.
    private static PfListingSearchResult ToSearchResult(PfListingDto dto) => new()
    {
        Id = dto.ListingId.GetHashCode(),
        ListingId = dto.ListingId,
        Name = dto.Name,
        Description = dto.Description,
        World = dto.World,
        DataCenter = dto.DataCenter,
        DutyName = dto.DutyName,
        Category = dto.Category,
        DutyType = dto.DutyType,
        MinItemLevel = dto.MinItemLevel,
        CapturedAt = dto.CapturedAt,
        SlotsAvailable = dto.SlotsAvailable,
        SlotsFilled = dto.SlotsFilled,
        JobsPresent = dto.JobsPresent,
        Tags = dto.Tags,
        OpenSlotJobs = dto.OpenSlotJobs,
    };

    // Called by PfScanTracker right after a batch of ReceiveListing events
    // from your own organic browsing (opening PF, switching a category tab,
    // or PfListingOpener prefetching a category before jumping to a
    // listing) settles with fewer than 50 listings total (the game's own
    // page cap — see PfScanTracker.MaxListingsPerPage) for one or more
    // display buckets in `buckets`. An unfilled page is a complete,
    // authoritative snapshot of every listing currently active in those
    // buckets for `dataCenter` — the only DC a local scan could have seen —
    // so anything already in Matches under the same buckets/DC that isn't
    // among `seenListingIds` has actually closed/expired in-game. Dropping
    // it here means the list updates instantly instead of sitting stale
    // until the next ~30s poll. Never touches other data centers' listings
    // (server-aggregated from other contributors) since this client has no
    // way to confirm whether those are still up.
    //
    // Matched by MatchCategorizer.NativeBucket, NOT CategoryBucket and NOT
    // the raw PfListingSearchResult.Category field either:
    //  - Raw Category alone: the High End Duty tab is the reason why —
    //    specific fights (see MatchCategorizer.HighEndDutyNames) get
    //    reclassified by duty name alone, while their raw Category field
    //    still reads "Trials"/"Raids" straight from the game (same field
    //    xivpf mirrors). Filtering on the raw field here would never match
    //    anything for that tab — an unfilled High End Duty scan would
    //    silently prune nothing, leaving stale entries (e.g. from
    //    xivpf.com) sitting there forever even after your own client
    //    proved they're gone.
    //  - CategoryBucket (the display bucket): collapses every BLU-eligible
    //    Dungeons/Trials/Raids/HighEndDuty listing into a single "BlueMage"
    //    value, so a scan of any one of those tabs couldn't tell which BLU
    //    listings it actually just proved gone — a BLU-flagged dungeon
    //    party staying stale forever even after you watched it vanish from
    //    the game's own Dungeons tab was exactly this bug. NativeBucket
    //    resolves a BLU-flagged listing back to the one real tab it
    //    actually lives under instead.
    public IReadOnlyList<string> PruneMissing(IReadOnlySet<string> buckets, string dataCenter, IReadOnlySet<string> seenListingIds)
    {
        var stale = Matches
            .Where(l => buckets.Contains(MatchCategorizer.NativeBucket(l))
                && string.Equals(l.DataCenter, dataCenter, StringComparison.OrdinalIgnoreCase)
                && !seenListingIds.Contains(l.ListingId))
            .ToList();

        // Missing from a complete, unfilled category page — corroborated,
        // high-confidence evidence, so this is allowed to also block the
        // next poll from silently bringing it right back (see
        // _locallyRemovedListingIds's own doc comment).
        var removed = RemoveStale(stale, suppressFromPolls: true).ToList();

        // Reconfirmation: a listing an EARLIER call to this method already
        // removed (and reported) for this same bucket/DC, still within its
        // grace window, that THIS fresh unfilled page also doesn't contain —
        // resend the expire report and refresh its grace, on the same
        // reasoning as _locallyRemovedListingIds's own doc comment on why
        // one report isn't always enough to actually get the server to mark
        // it gone. Every caller of PruneMissing already sends whatever this
        // returns to ExpireAsync, so folding these into the same return
        // value is all that's needed to get them resent — no separate call
        // site.
        var now = DateTime.UtcNow;
        var reconfirmed = _locallyRemovedListingIds
            .Where(kv => kv.Value.ExpiresAt > now
                && buckets.Contains(kv.Value.Bucket)
                && string.Equals(kv.Value.DataCenter, dataCenter, StringComparison.OrdinalIgnoreCase)
                && !seenListingIds.Contains(kv.Key))
            .Select(kv => kv.Key)
            .ToList();

        if (reconfirmed.Count > 0)
        {
            var expiresAt = now + LocalRemovalGrace;
            foreach (var listingId in reconfirmed)
            {
                var existing = _locallyRemovedListingIds[listingId];
                _locallyRemovedListingIds[listingId] = existing with { ExpiresAt = expiresAt };
            }

            removed.AddRange(reconfirmed);
        }

        return removed;
    }

    // Same removal as PruneMissing, for a single listing flagged by some
    // OTHER, weaker signal — currently just PfListingOpener, when a
    // listing's detail popup doesn't appear shortly after being clicked
    // (see its own doc comment). That's suggestive, not confirmed: it could
    // just as easily be lag as the listing actually being gone, unlike
    // PruneMissing's "absent from an entire unfilled category page." So
    // this fixes the results list instantly (the whole point — don't leave
    // a dead-looking entry sitting there) but deliberately does NOT set
    // suppressFromPolls — a false positive here should self-heal on the
    // very next poll instead of blocking the server's own (correct) data
    // for the full 10-minute grace window, which is what a bad detail-open
    // read used to do.
    public IReadOnlyList<string> RemoveListingLocally(string listingId)
    {
        var stale = Matches.Where(l => l.ListingId == listingId).ToList();
        return RemoveStale(stale, suppressFromPolls: false);
    }

    private IReadOnlyList<string> RemoveStale(List<PfListingSearchResult> stale, bool suppressFromPolls)
    {
        if (stale.Count == 0)
            return Array.Empty<string>();

        var staleSet = stale.Select(l => l.ListingId).ToHashSet();
        Matches = Matches.Where(l => !staleSet.Contains(l.ListingId)).ToList();
        _previousMatchingIds?.ExceptWith(staleSet);
        if (_previousSlotsFilled != null)
            foreach (var id in staleSet)
                _previousSlotsFilled.Remove(id);
        if (NewMatchIds.Count > 0)
            NewMatchIds = NewMatchIds.Where(id => !staleSet.Contains(id)).ToHashSet();
        foreach (var id in staleSet)
            _missingPollStreak.Remove(id);

        // Also block this exact listing from being resurrected by the next
        // poll (see _locallyRemovedListingIds's doc comment) — only for the
        // high-confidence caller (PruneMissing); see RemoveListingLocally's
        // own doc comment for why the weaker-signal caller skips this.
        if (suppressFromPolls)
        {
            var expiresAt = DateTime.UtcNow + LocalRemovalGrace;
            foreach (var listing in stale)
                _locallyRemovedListingIds[listing.ListingId] =
                    new LocalRemoval(expiresAt, MatchCategorizer.NativeBucket(listing), listing.DataCenter);
        }

        // Instant version of PollAsync's own removed-announcement — this
        // is the higher-confidence path (ground truth from the game
        // itself, not just "the server stopped returning it"), so it fires
        // right away instead of waiting for the next poll to notice the
        // same absence (which it won't, since the lines above already
        // scrub these ids out of _previousMatchingIds before that diff
        // ever runs).
        if (_config.AlertNotifyOnRemoved)
        {
            foreach (var listing in stale.Where(PassesDisplayFilters).Take(MaxAnnouncementsPerPoll))
                AnnounceRemoved(listing);
        }

        return staleSet.ToList();
    }

    private bool Matches_(PfListingSearchResult listing)
    {
        if (_config.AlertDataCenters.Count > 0 && !_config.AlertDataCenters.Contains(listing.DataCenter))
            return false;

        if (_config.AlertIlvlMin > 0 && listing.MinItemLevel < _config.AlertIlvlMin)
            return false;
        if (_config.AlertIlvlMax > 0 && listing.MinItemLevel > _config.AlertIlvlMax)
            return false;

        if (_config.AlertJobs.Count == 0)
            return true;

        // Same semantics as the website's hasOpenSlotForJobs: an unrestricted
        // seat (empty accepted-job list) matches any job; otherwise it's a
        // direct membership check against that seat's accepted jobs. BLU is
        // the one exception — it's locked out of virtually all normal duty
        // content, so an "unrestricted" seat never actually accepts a Blue
        // Mage, matching the website's own hasOpenSlotForJobs behavior.
        var openCount = Math.Max(0, listing.SlotsAvailable - listing.SlotsFilled);
        return listing.OpenSlotJobs
            .Take(openCount)
            .Any(accepted => accepted.Count == 0
                ? _config.AlertJobs.Any(j => j != "BLU")
                : accepted.Any(_config.AlertJobs.Contains));
    }

    // UIColor row 1 — a genuine 0xFFFFFFFF pure white (verified the same way
    // as AlertRemovedColor's 518: read the actual sheet rather than
    // guessing), used to make the duty name and slot count stand out from
    // the rest of the message regardless of whatever ambient color a
    // player's chat channel/theme normally renders default text in.
    private const ushort WhiteColor = 1;

    // Exposed for StatusWindow's Debug tab "test" button — same call path a
    // real match/change/removal fires, just with synthetic data, so what
    // you see there is exactly what a real notification would look like.
    // All three in one call (rather than separate buttons) since there's
    // nothing to configure differently between them — just fire and look.
    public void TestAllAnnouncements()
    {
        AnnounceNewMatch(SampleListing());
        AnnouncePartyChange(SampleListing(), previousSlotsFilled: 4);
        AnnounceRemoved(SampleListing());
    }

    // Uses your actual current data center (falling back to a fixed one if
    // it can't be resolved, e.g. not logged in) rather than a hardcoded
    // value — a hardcoded DC here would make the test announcements
    // silently non-clickable for anyone not standing on that exact one,
    // since SendNotification only attaches a link for same-DC listings.
    private static PfListingSearchResult SampleListing() => new()
    {
        Id = -1,
        ListingId = "0",
        Name = "Test Recruiter",
        World = "Excalibur",
        DataCenter = Windows.MatchListView.GetLocalDataCenter() ?? "Primal",
        DutyName = "Zoraal Ja (Extreme)",
        Category = "Trials",
        SlotsFilled = 5,
        SlotsAvailable = 8,
        CapturedAt = DateTime.UtcNow.ToString("o"),
    };

    private void AnnounceNewMatch(PfListingSearchResult listing) =>
        SendNotification(MatchCategorizer.BuildNewMatchAnnouncement(listing), MatchCategorizer.NewMatchTag, _config.AlertNewMatchColor, listing);

    private void AnnouncePartyChange(PfListingSearchResult listing, int previousSlotsFilled) =>
        SendNotification(MatchCategorizer.BuildPartyChangeAnnouncement(listing, previousSlotsFilled), MatchCategorizer.PartyChangeTag, _config.AlertPartyChangeColor, listing);

    private void AnnounceRemoved(PfListingSearchResult listing) =>
        SendNotification(MatchCategorizer.BuildRemovedAnnouncement(listing), MatchCategorizer.RemovedTag, _config.AlertRemovedColor, listing);

    // Any combination of chat/toast/sound, independently — delivery method
    // is separate from which events trigger a notification in the first
    // place (AlertNotifyOnNewMatch/AlertNotifyOnPartyChange, checked by the
    // callers above before this is even reached).
    private void SendNotification(string message, string tag, ushort colorKey, PfListingSearchResult listing)
    {
        if (!_config.AlertNotifyChat && !_config.AlertNotifyToast && !_config.AlertNotifySound)
            return;

        if (_config.AlertNotifyChat)
        {
            // Always clickable now — PfListingOpener itself handles a
            // different-DC listing (prompts travel if the region allows it,
            // shows an error toast if not), so there's no case left where
            // clicking would silently do nothing.
            _chatGui.Print(BuildColoredMessage(message, tag, colorKey, ClaimLinkPayload(listing)));
        }

        if (_config.AlertNotifyToast)
        {
            // ShowNormal's toast sits low on screen and is easy to miss —
            // ShowQuest is the same big, top-of-screen banner the game uses
            // for "Quest complete"/duty pop-style notices, much harder to
            // miss glancing away from the PF window. No color here — it's
            // a bold gold banner by the game's own styling regardless of
            // any color payload, so there's nothing to color.
            _toastGui.ShowQuest(message);
        }

        if (_config.AlertNotifySound)
        {
            try
            {
                // A generic Windows notification sound rather than a
                // bundled audio asset — simplest reliable option without
                // shipping/loading a .wav file ourselves.
                MessageBeep(MB_ICONASTERISK);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "[PfExplorer] failed to play alert sound");
            }
        }
    }

    // Builds the actual colored chat payload — three independently-colored
    // spans layered onto MatchCategorizer's plain message string:
    //   - "<Duty Tab> (tag)" in colorKey (ties the category to the event
    //     type at a glance, not just the tag floating alone mid-line).
    //   - the quoted duty name in WhiteColor.
    //   - the trailing slot-count group ("(5/8)" or "(5/8 -> 6/8)") in
    //     WhiteColor, when the message has one — BuildRemovedAnnouncement's
    //     messages don't, since a gone listing has no current party size.
    // Everything else stays plain (default chat color). When `link` is
    // non-null, the whole line is additionally wrapped in it — clicking
    // anywhere on the message calls that payload's handler (see
    // ClaimLinkPayload), same as a native chat link.
    private static SeString BuildColoredMessage(string message, string tag, ushort colorKey, DalamudLinkPayload? link)
    {
        var tagIndex = message.IndexOf(tag, StringComparison.Ordinal);
        if (tagIndex < 0)
        {
            var plainBuilder = new SeStringBuilder();
            if (link != null) plainBuilder.Add(link);
            plainBuilder.AddText(message);
            if (link != null) plainBuilder.Add(RawPayload.LinkTerminator);
            return plainBuilder.Build();
        }

        var tagEnd = tagIndex + tag.Length;

        // The duty name is always the first "..."-quoted span. The slot
        // count, when present, is always the message's own final
        // parenthesized group — DutyName can itself contain parens (e.g.
        // "Zoraal Ja (Extreme)"), but those sit inside the quotes, earlier
        // in the string, so "last '(' in the whole message" still lands on
        // the count group whenever one exists rather than on those.
        var quoteStart = message.IndexOf('"', tagEnd);
        var quoteEnd = quoteStart >= 0 ? message.IndexOf('"', quoteStart + 1) : -1;
        var dutyNameEnd = quoteEnd >= 0 ? quoteEnd + 1 : -1;

        // Checked against the actual shape ("5/8" or "5/8 -> 6/8"), not just
        // "some trailing (...)" — a removed-listing message has no count
        // group at all, and without this its trailing "(World)" (the last
        // parenthetical in that message) would get mistaken for one and
        // colored white along with it.
        var countStart = message.LastIndexOf('(');
        var hasCount = countStart >= 0 && countStart > dutyNameEnd && message.EndsWith(")", StringComparison.Ordinal)
            && Regex.IsMatch(message[(countStart + 1)..^1], @"^\d+/\d+( -> \d+/\d+)?$");

        var builder = new SeStringBuilder();
        if (link != null)
            builder.Add(link);

        var cursor = 0;

        void AppendPlain(int end)
        {
            if (end > cursor)
                builder.AddText(message[cursor..end]);
        }

        // From the very start of the line, not just from tagIndex — colors
        // "<Duty Tab> (gone)" together (e.g. "High End Duty (gone)"), same
        // as before this method's white-span support was added.
        builder.AddUiForeground(colorKey).AddText(message[cursor..tagEnd]).AddUiForegroundOff();
        cursor = tagEnd;

        if (dutyNameEnd > cursor)
        {
            AppendPlain(quoteStart);
            builder.AddUiForeground(WhiteColor).AddText(message[quoteStart..dutyNameEnd]).AddUiForegroundOff();
            cursor = dutyNameEnd;
        }

        if (hasCount)
        {
            AppendPlain(countStart);
            builder.AddUiForeground(WhiteColor).AddText(message[countStart..]).AddUiForegroundOff();
            cursor = message.Length;
        }

        AppendPlain(message.Length);
        if (link != null)
            builder.Add(RawPayload.LinkTerminator);
        return builder.Build();
    }

    public void Dispose()
    {
        _timer.Dispose();
        _http.Dispose();
        // Removes every commandId this instance registered in the pool
        // above — RemoveChatLinkHandler() with no id removes all of this
        // plugin's handlers at once, so a reload doesn't leave the old
        // instance's handlers (closing over its now-disposed state) live.
        _chatGui.RemoveChatLinkHandler();
    }
}
