using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.Text.SeStringHandling;
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

    // Null until the first poll completes, so enabling alerts mid-session
    // doesn't flag every match that was already up as "new".
    private HashSet<int>? _previousMatchingIds;

    // Every listing id that's ever been seen in a matching poll, kept
    // forever (until ResetBaseline) — unlike _previousMatchingIds, entries
    // are never removed just because a listing drops out of one poll's
    // results. Without this, a listing that ages past the freshness
    // window (or briefly falls out due to a captured_at edge/dedupe race
    // between a plugin- and xivpf-sourced copy of the same real-world
    // listing) then gets recaptured looks like a brand new match to
    // _previousMatchingIds and fires a duplicate "new match" notification
    // even though nothing actually changed in-game. This set is the
    // source of truth for "have we already announced this one", separate
    // from NewMatchIds (which stays poll-to-poll and only drives the
    // list's transient "new" highlight).
    private readonly HashSet<int> _announcedMatchIds = new();

    // ListingId -> when this suppression expires. Populated by PruneMissing
    // when a complete-page scan confirms a listing is actually gone from PF
    // — without this, the very next poll (its own ~30s timer, independent
    // of the scan cycle) would just silently bring it right back, since the
    // server's own "active" definition lags well behind what the game
    // itself just told us: a row only drops out of activeOnly results once
    // its updated_at ages past LISTING_FRESHNESS_MINUTES (5min by default),
    // and a listing double-sourced via xivpfSync.ts's independent re-scrape
    // sits under a completely separate DB row that can stay "active" even
    // longer if xivpf.com's own data hasn't caught up yet. Cleared the
    // moment a scan sees the listing again (see RefreshFromScan), so a
    // false prune self-heals within one scan cycle instead of being stuck
    // hidden for the full grace window below.
    private readonly Dictionary<string, DateTime> _locallyRemovedListingIds = new();
    private static readonly TimeSpan LocalRemovalGrace = TimeSpan.FromMinutes(10);

    // listing id -> slotsFilled as of the last poll, for detecting a party
    // size change (5/8 -> 6/8) on a listing that was already matching.
    // Tracked for every match, not just filtered/announced ones, so the
    // diff is always correct regardless of what AlertNotifyOnPartyChange is
    // set to at any given moment.
    private Dictionary<int, int>? _previousSlotsFilled;

    // Guards against the manual refresh button and the timer firing a poll
    // at the same time and racing each other.
    private volatile bool _isPolling;
    public bool CanManualRefresh => _config.AlertEnabled && !_isPolling;

    public IReadOnlyList<PfListingSearchResult> Matches { get; private set; } = Array.Empty<PfListingSearchResult>();

    // Listing IDs that weren't in the previous poll's matching set — the
    // window highlights these instead of firing an individual toast per one.
    public IReadOnlySet<int> NewMatchIds { get; private set; } = new HashSet<int>();

    public DateTime? LastPollAt { get; private set; }

    // Approximate — the underlying Timer fires on its own fixed
    // PollIntervalMs cadence regardless of manual RequestPoll() calls in
    // between, so this is "assuming nothing pokes it early" rather than an
    // authoritative next-fire time. Good enough for the "Refresh in Xs"
    // display next to the manual Refresh button.
    public DateTime? NextPollAt => LastPollAt?.AddMilliseconds(PollIntervalMs);
    public string? LastError { get; private set; }

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
                             .Where(kv => kv.Value <= now)
                             .Select(kv => kv.Key)
                             .ToList())
                    _locallyRemovedListingIds.Remove(expiredId);

                if (_locallyRemovedListingIds.Count > 0)
                    listings = listings.Where(l => !_locallyRemovedListingIds.ContainsKey(l.ListingId)).ToList();
            }

            // This poll's server snapshot only reflects whatever was last
            // actually uploaded — it doesn't know about PfBackgroundScraper's
            // scan-driven RefreshFromScan updates, which only ever touch the
            // local copy. Without this, every ~30s poll (running on its own
            // timer, independent of the scan cycle) would stomp straight
            // back over a locally fresher CapturedAt/slot state with this
            // now-stale one. Keep whichever side actually saw the listing
            // more recently.
            // Snapshotted before Matches gets overwritten below — needed to
            // build a removed-listing announcement (duty/recruiter/world),
            // since that data won't exist in this poll's response anymore.
            var previousMatchesById = Matches.ToDictionary(l => l.Id);
            var previousByListingId = Matches.ToDictionary(l => l.ListingId);
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

            var matching = listings.Where(Matches_).ToList();
            var matchingIds = matching.Select(l => l.Id).ToHashSet();

            var previousIds = _previousMatchingIds;
            var previousSlots = _previousSlotsFilled;
            NewMatchIds = previousIds == null ? new HashSet<int>() : matchingIds.Where(id => !previousIds.Contains(id)).ToHashSet();
            Matches = matching;
            _previousMatchingIds = matchingIds;
            _previousSlotsFilled = matching.ToDictionary(l => l.Id, l => l.SlotsFilled);
            LastPollAt = DateTime.UtcNow;
            LastError = null;

            // Seeded every poll (not just for ones that get announced) so a
            // listing that's currently hidden by the category/freshness
            // display filter doesn't later "become new" the moment it starts
            // passing that filter — see _announcedMatchIds's doc comment.
            foreach (var id in matchingIds)
                _announcedMatchIds.Add(id);

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
                        .Where(l => !_announcedMatchIds.Contains(l.Id))
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
                        .Where(l => !NewMatchIds.Contains(l.Id))
                        .Where(l => previousSlots.TryGetValue(l.Id, out var prevFilled) && prevFilled != l.SlotsFilled)
                        .Where(PassesDisplayFilters)
                        .Take(MaxAnnouncementsPerPoll);

                    foreach (var listing in changed)
                        AnnouncePartyChange(listing, previousSlots[listing.Id]);
                }

                if (_config.AlertNotifyOnRemoved)
                {
                    // Ids that were matching last poll and aren't anymore —
                    // covers a listing aging out of the server's own active
                    // window (freshness/expires_at) or getting expired via
                    // the verified-deletion consensus, for DCs the local
                    // scan can't directly confirm. A scan-confirmed removal
                    // (PruneMissing) announces separately and immediately;
                    // by the time this runs, PruneMissing has already
                    // scrubbed that id out of previousIds too, so there's no
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

    // Called by PfBackgroundScraper for every scan (complete page or not —
    // a listing this actually saw is trustworthy regardless of how many
    // others came back alongside it) to fold fresh data straight into any
    // Match already tracking that same listing, instead of leaving it to
    // show party size/tags/CapturedAt as they were as of the last ~30s
    // poll. Mutates the matched PfListingSearchResult objects in place
    // (they're reference types with public setters, already sitting in
    // Matches) rather than rebuilding the list, so this doesn't disturb
    // NewMatchIds/_previousMatchingIds bookkeeping at all. Scoped to
    // `dataCenter` for the same reason as PruneMissing below — a scan can
    // only ever see the player's own DC.
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
            updated++;
        }

        return updated;
    }

    // Called by PfBackgroundScraper right after a category scan comes back
    // with fewer than 50 listings (the game's own page cap — see
    // PfBackgroundScraper.MaxListingsPerPage) for one of the raw categories
    // in `rawCategories`. An unfilled page is a complete, authoritative
    // snapshot of every listing currently active in those categories for
    // `dataCenter` — the only DC the scan itself could have seen — so
    // anything already in Matches under the same categories/DC that isn't
    // among `seenListingIds` has actually closed/expired in-game. Dropping
    // it here means the list updates instantly instead of sitting stale
    // until the next ~30s poll. Never touches other data centers' listings
    // (server-aggregated from other contributors) since this client has no
    // way to confirm whether those are still up.
    public IReadOnlyList<string> PruneMissing(IReadOnlySet<string> rawCategories, string dataCenter, IReadOnlySet<string> seenListingIds)
    {
        var stale = Matches
            .Where(l => rawCategories.Contains(l.Category)
                && string.Equals(l.DataCenter, dataCenter, StringComparison.OrdinalIgnoreCase)
                && !seenListingIds.Contains(l.ListingId))
            .ToList();

        if (stale.Count == 0)
            return Array.Empty<string>();

        var staleSet = stale.Select(l => l.Id).ToHashSet();
        Matches = Matches.Where(l => !staleSet.Contains(l.Id)).ToList();
        _previousMatchingIds?.ExceptWith(staleSet);
        if (_previousSlotsFilled != null)
            foreach (var id in staleSet)
                _previousSlotsFilled.Remove(id);
        if (NewMatchIds.Count > 0)
            NewMatchIds = NewMatchIds.Where(id => !staleSet.Contains(id)).ToHashSet();

        // Also block this exact listing from being resurrected by the next
        // poll (see _locallyRemovedListingIds's doc comment) — the game
        // confirmed it's gone, so the server catching up later doesn't get
        // to overrule that until either the grace window elapses or a scan
        // sees it again.
        var expiresAt = DateTime.UtcNow + LocalRemovalGrace;
        var staleListingIds = stale.Select(l => l.ListingId).ToList();
        foreach (var listingId in staleListingIds)
            _locallyRemovedListingIds[listingId] = expiresAt;

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

        return staleListingIds;
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

    // UIColor sheet row IDs — vivid/saturated (rgb per xivapi's decoded
    // UIColor.UIForeground): 45 = (0,204,34) green, 37 = (0,153,255) blue.
    // Red is NOT the original 17 — checked, and 17 has no value in the
    // sheet at all (same problem 518 had during the muted detour), so it
    // was never actually rendering red this whole time regardless of what
    // it was set to. 19 = (68,11,0) is the only row with a fully-saturated
    // (sat 1.0) genuine red — darker in absolute terms since that's just
    // what the sheet has, but it's real saturated red, not a broken lookup.
    private const ushort NewMatchColor = 45; // green
    private const ushort PartyChangeColor = 37; // blue
    private const ushort RemovedColor = 19; // red

    private void AnnounceNewMatch(PfListingSearchResult listing) =>
        SendNotification(MatchCategorizer.BuildNewMatchAnnouncement(listing), MatchCategorizer.NewMatchTag, NewMatchColor);

    private void AnnouncePartyChange(PfListingSearchResult listing, int previousSlotsFilled) =>
        SendNotification(MatchCategorizer.BuildPartyChangeAnnouncement(listing, previousSlotsFilled), MatchCategorizer.PartyChangeTag, PartyChangeColor);

    private void AnnounceRemoved(PfListingSearchResult listing) =>
        SendNotification(MatchCategorizer.BuildRemovedAnnouncement(listing), MatchCategorizer.RemovedTag, RemovedColor);

    // Any combination of chat/toast/sound, independently — delivery method
    // is separate from which events trigger a notification in the first
    // place (AlertNotifyOnNewMatch/AlertNotifyOnPartyChange, checked by the
    // callers above before this is even reached).
    private void SendNotification(string message, string tag, ushort colorKey)
    {
        if (!_config.AlertNotifyChat && !_config.AlertNotifyToast && !_config.AlertNotifySound)
            return;

        if (_config.AlertNotifyChat)
        {
            // Only the "(new)"/"(changed)"/"(gone)" tag itself is colored —
            // coloring the whole line made every announcement read as one
            // solid color block instead of the tag standing out against
            // normal chat text.
            var tagIndex = message.IndexOf(tag, StringComparison.Ordinal);
            var builder = new SeStringBuilder();
            if (tagIndex < 0)
            {
                builder.AddText(message);
            }
            else
            {
                builder.AddText(message[..tagIndex])
                    .AddUiForeground(colorKey)
                    .AddText(tag)
                    .AddUiForegroundOff()
                    .AddText(message[(tagIndex + tag.Length)..]);
            }

            _chatGui.Print(builder.Build());
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

    public void Dispose()
    {
        _timer.Dispose();
        _http.Dispose();
    }
}
