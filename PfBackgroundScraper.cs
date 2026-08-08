using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using PfExplorer.Models;
using PfExplorer.Windows;

namespace PfExplorer;

// Periodically requests a fixed set of PF categories via
// AgentLookingForGroup.RequestCategoryListings — the same request the game
// sends when you click a category tab yourself, which fires
// IPartyFinderGui.ReceiveListing for everything each one returns. This is
// what actually keeps the server fed with fresh, broad capture data,
// instead of relying on players manually browsing PF or clicking through
// their own alert list (see PfListingOpener, which only opens one specific
// listing and doesn't trigger a real search on its own).
//
// Only runs while the native PF window itself is closed — firing
// RequestCategoryListings resets CategoryTab, which would yank the
// category/page out from under you mid-browse if the window were open.
//
// The byte RequestCategoryListings expects is NOT DutyCategory's own enum
// value (that's a bitflag layout used elsewhere — FieldOperations = 16384,
// DeepDungeons = 8192, etc.) — it's just the category's plain 1-based
// position in the in-game tab order, live-confirmed (index 4 = Trials, as
// reported after trying it). 0, 255, and RequestListingsUpdate() (which
// takes no category and appears to just re-request whatever the previous
// call set, rather than being a distinct "All") were all tried live and
// none behaved like "everything" — there doesn't seem to be a numbered
// "All" at all. So instead of chasing one, this just walks all 16 real
// categories every cycle, which amounts to the same coverage — spread out
// randomly across roughly every CycleWindow, in a random order, and
// probabilistically skipping categories that were empty last time (see
// RandomStepOffsets/ShuffleStepOrder/SkipEmptyCategoryChance below) rather
// than firing all 16 back-to-back on a fixed schedule.
public sealed class PfBackgroundScraper
{
    private static readonly (byte? CategoryValue, string Label)[] Categories =
    {
        (1, "Roulette"),
        (2, "Dungeons"),
        (3, "GuildQuests"),
        (4, "Trials"),
        (5, "Raids"),
        (6, "HighEndDuty"),
        (7, "PvP"),
        (8, "GoldSaucer"),
        (9, "FATEs"),
        (10, "TreasureHunts"),
        (11, "TheHunt"),
        (12, "GatheringForays"),
        (13, "DeepDungeons"),
        (14, "FieldOperations/Occult"),
        (15, "VCDungeonFinder"),
        (16, "Other"),
    };

    // One full walk (up to 16 requests) is spread across this whole window
    // instead of firing back-to-back — see RandomStepOffsets. Each cycle's
    // actual length is this ± jitter (below), so it isn't even a clockwork
    // 2:00 between walks.
    private static readonly TimeSpan CycleWindow = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ResultSampleDelay = TimeSpan.FromSeconds(1.5);
    private const int MaxHistoryEntries = 40;

    // Most categories are empty most of the time (PvP/TheHunt/GoldSaucer at
    // odd hours, etc) — skip actually firing a request for one whose last
    // real check came back with nothing, most of the time, instead of
    // spending a request on content that's unlikely to have changed. Never
    // skipped for certain (< 1.0) so a category that repopulates later
    // still gets rediscovered rather than staying permanently ignored.
    // There's no way to know a category's count before requesting it — the
    // game's own PF tab list doesn't show per-category counts either, you
    // only find out by actually opening/searching that tab — so this is
    // only ever as fresh as our own last observation, not real-time.
    private const double SkipEmptyCategoryChance = 0.8;

    // The game caps a single RequestCategoryListings response at 50
    // listings — a page that comes back under that isn't truncated, so it's
    // the full, current set for that category rather than just the first
    // slice of a longer one. Only an unfilled page like that is safe to
    // treat as "if it's not in here, it's gone" for pruning.
    private const int MaxListingsPerPage = 50;

    // Best-effort mapping from a request's Label (above) to the raw
    // PfListingDto.Category value(s) it's expected to return — this, and
    // only this, is what SyncWithScan trusts a given request to have
    // comprehensively covered for pruning purposes. Deliberately NOT
    // widened by whatever raw categories happen to show up in the actual
    // response (that was tried and caused real false prunes/expire
    // reports — see SyncWithScan's comment) — a wrong/incomplete guess
    // here just means pruning silently no-ops for that raw category from
    // this request, not a bad prune. FieldOperations/Occult is the one
    // request that covers two distinct raw categories at once.
    private static readonly Dictionary<string, string[]> ExpectedRawCategories = new()
    {
        ["Roulette"] = new[] { "Roulette" },
        ["Dungeons"] = new[] { "Dungeons" },
        ["GuildQuests"] = new[] { "GuildQuests" },
        ["Trials"] = new[] { "Trials" },
        ["Raids"] = new[] { "Raids" },
        ["HighEndDuty"] = new[] { "HighEndDuty" },
        ["PvP"] = new[] { "PvP" },
        ["GoldSaucer"] = new[] { "GoldSaucer" },
        ["FATEs"] = new[] { "FATEs" },
        ["TreasureHunts"] = new[] { "TreasureHunts" },
        ["TheHunt"] = new[] { "TheHunt" },
        ["GatheringForays"] = new[] { "GatheringForays" },
        ["DeepDungeons"] = new[] { "DeepDungeons" },
        ["FieldOperations/Occult"] = new[] { "FieldOperations", "OccultCrescent" },
        ["VCDungeonFinder"] = new[] { "VCDungeonFinder" },
        ["Other"] = new[] { "Other" },
    };

    // +/-15% jitter on every scheduled cycle — same reasoning as
    // AlertPoller.JitterFraction: many clients launched around the same
    // moment shouldn't all walk categories (and burst-upload whatever they
    // capture) in lockstep every 60s forever.
    private const double JitterFraction = 0.15;
    private static readonly Random JitterRandom = new();

    private readonly Configuration _config;
    private readonly AlertPoller _alertPoller;
    private readonly ListingUploader _uploader;
    private DateTime _nextCycleAt = DateTime.UtcNow + JitteredDelay(TimeSpan.FromSeconds(15));
    private DateTime _cycleStartAt = DateTime.UtcNow;
    private DateTime? _pendingResultCheckAt;
    private string _pendingLabel = "";
    private string _pendingCategoryLabel = "";
    private string? _firstListingSinceRequest;
    private int _listingsSeenSinceRequest;
    private readonly List<PfListingDto> _listingsSinceRequest = new();
    private int _stepIndex = Categories.Length; // idle until the first cycle starts
    // Which Categories index each step position walks this cycle — reshuffled
    // at the start of every cycle (see Tick) so the 16 requests don't fire in
    // the exact same 1..16 order forever. A real player clicking through PF
    // tabs doesn't do that either, so not matching it makes the traffic
    // pattern that much less mechanically obvious.
    private readonly int[] _stepOrder = Enumerable.Range(0, Categories.Length).ToArray();
    // When (relative to _cycleStartAt) each step position actually fires —
    // regenerated every cycle by RandomStepOffsets as random, unsorted-by-
    // construction-but-sorted-for-use points within the cycle's length, not
    // evenly spaced StepPacing apart like a fixed-interval timer would be.
    private TimeSpan[] _stepOffsets = Array.Empty<TimeSpan>();
    // label -> listing count as of the last time that category was actually
    // requested (not just scheduled) — see SkipEmptyCategoryChance.
    private readonly Dictionary<string, int> _lastKnownCount = new();
    private readonly List<ScanResult> _history = new();

    public PfBackgroundScraper(Configuration config, AlertPoller alertPoller, ListingUploader uploader)
    {
        _config = config;
        _alertPoller = alertPoller;
        _uploader = uploader;
    }

    // The scheduled time of the next full walk — for the Upload tab's
    // "Next scan in..." countdown.
    public DateTime NextActionAt => _nextCycleAt;

    // Most recent scan first — for the Debug tab's scan log.
    public IReadOnlyList<ScanResult> History => _history;

    // Called from Plugin.OnReceiveListing for every listing the game hands
    // back, regardless of source — tallies how many actually arrived since
    // the last request fired, and records the first one's name. This is
    // ground truth (it's the exact event our own capture pipeline runs
    // off), unlike NumberOfListingsDisplayed (a UI-rendering counter that
    // stays 0 while the addon isn't open and drawing — which is always,
    // since this only scans while it's closed) or Listings.ListingIds
    // (fixed-size native buffer; .Length is its allocated capacity, not
    // how many entries are actually populated).
    public void NotifyListingReceived(PfListingDto dto)
    {
        var name = string.IsNullOrEmpty(dto.DutyName) ? dto.Name : dto.DutyName;
        _firstListingSinceRequest ??= name;
        _listingsSeenSinceRequest++;
        _listingsSinceRequest.Add(dto);
    }

    // Pumped from Plugin's Draw hook every frame — cheap when idle (a
    // couple of date/pointer checks), so safe to call unconditionally
    // rather than needing its own timer/thread.
    public unsafe void Tick()
    {
        if (!_config.AlertBackgroundScraperEnabled)
            return;

        // Party Finder itself is unreachable while logged out, mid-duty, or
        // between zones/cutscenes — RequestCategoryListings still "succeeds"
        // in those states, it just never yields ReceiveListing calls, so
        // skip requesting until the game would actually let you open PF.
        if (!Plugin.ClientState.IsLoggedIn)
            return;

        if (Plugin.Condition[ConditionFlag.BoundByDuty]
            || Plugin.Condition[ConditionFlag.BoundByDuty56]
            || Plugin.Condition[ConditionFlag.BoundByDuty95]
            || Plugin.Condition[ConditionFlag.BetweenAreas]
            || Plugin.Condition[ConditionFlag.BetweenAreas51]
            || Plugin.Condition[ConditionFlag.WatchingCutscene]
            || Plugin.Condition[ConditionFlag.WatchingCutscene78]
            || Plugin.Condition[ConditionFlag.OccupiedInCutSceneEvent])
            return;

        // Never touch the agent while the player has PF open themselves —
        // RequestCategoryListings resets CategoryTab, which would silently
        // yank whatever category/page they're actively browsing.
        if (Plugin.GameGui.GetAddonByName("LookingForGroup", 1).Address != IntPtr.Zero)
            return;

        var now = DateTime.UtcNow;

        if (_pendingResultCheckAt is { } checkAt && now >= checkAt)
        {
            _pendingResultCheckAt = null;
            RecordScan(now, _pendingLabel, _listingsSeenSinceRequest, _firstListingSinceRequest);
            _lastKnownCount[_pendingCategoryLabel] = _listingsSeenSinceRequest;
            SyncWithScan();
        }

        if (_stepIndex >= Categories.Length)
        {
            if (now < _nextCycleAt)
                return;

            var cycleLength = JitteredDelay(CycleWindow);
            _stepIndex = 0;
            _cycleStartAt = now;
            _stepOffsets = RandomStepOffsets(cycleLength, Categories.Length);
            _nextCycleAt = now + cycleLength;
            ShuffleStepOrder(_stepOrder);
        }

        if (now < _cycleStartAt + _stepOffsets[_stepIndex])
            return;

        var (categoryValue, label) = Categories[_stepOrder[_stepIndex]];

        if (_lastKnownCount.TryGetValue(label, out var lastCount)
            && lastCount == 0
            && JitterRandom.NextDouble() < SkipEmptyCategoryChance)
        {
            RecordScan(now, $"Skipped({label}) — empty last check", 0, null);
            _stepIndex++;
            return;
        }

        var requestAgent = AgentLookingForGroup.Instance();
        if (requestAgent != null)
        {
            if (categoryValue is { } category)
                requestAgent->RequestCategoryListings(category);
            else
                requestAgent->RequestListingsUpdate();
        }

        _pendingLabel = categoryValue is { } v
            ? $"RequestCategoryListings({label}={v})"
            : $"RequestListingsUpdate({label})";
        _pendingCategoryLabel = label;
        _firstListingSinceRequest = null;
        _listingsSeenSinceRequest = 0;
        _listingsSinceRequest.Clear();
        _pendingResultCheckAt = now + ResultSampleDelay;

        _stepIndex++;
    }

    private static TimeSpan JitteredDelay(TimeSpan baseDelay)
    {
        var jitterMs = (JitterRandom.NextDouble() * 2 - 1) * baseDelay.TotalMilliseconds * JitterFraction;
        return baseDelay + TimeSpan.FromMilliseconds(jitterMs);
    }

    // Fisher-Yates in place — called once per cycle (Tick, above) rather
    // than reshuffled per step, so a given cycle's order is set once and
    // just walked through, same structure as before, just not 1..16 anymore.
    private static void ShuffleStepOrder(int[] order)
    {
        for (var i = order.Length - 1; i > 0; i--)
        {
            var j = JitterRandom.Next(i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }
    }

    // `count` random points within [0, window), sorted ascending and at
    // least MinStepGap apart — genuinely random spacing between consecutive
    // steps (some gaps tiny-but-not-too-tiny, some huge), not just jitter
    // wobbling around a fixed average interval. Regenerated fresh every
    // cycle (Tick), same as _stepOrder.
    //
    // Enforced by shrinking the range random points are drawn from by
    // MinStepGap * (count - 1) — the total space that needs to be reserved
    // between them — then spreading that reserved gap back in once sorted
    // (offsets[i] gets pushed out by i * MinStepGap). That keeps every
    // consecutive pair >= MinStepGap apart without biasing WHERE in the
    // window the points land, unlike e.g. dividing the window into count
    // even slots and jittering within each.
    private static readonly TimeSpan MinStepGap = TimeSpan.FromSeconds(2);

    private static TimeSpan[] RandomStepOffsets(TimeSpan window, int count)
    {
        var offsets = new TimeSpan[count];
        if (count <= 1)
        {
            if (count == 1)
                offsets[0] = TimeSpan.FromMilliseconds(JitterRandom.NextDouble() * window.TotalMilliseconds);
            return offsets;
        }

        var reserved = MinStepGap.TotalMilliseconds * (count - 1);
        // Window should always have enough room in practice (CycleWindow is
        // minutes, MinStepGap*count is tens of seconds at most) — clamped to
        // 0 rather than going negative just in case CycleWindow or Categories
        // ever shrink enough to make that not true.
        var usableRangeMs = Math.Max(0, window.TotalMilliseconds - reserved);

        for (var i = 0; i < count; i++)
            offsets[i] = TimeSpan.FromMilliseconds(JitterRandom.NextDouble() * usableRangeMs);
        Array.Sort(offsets);

        for (var i = 0; i < count; i++)
            offsets[i] += TimeSpan.FromMilliseconds(i * MinStepGap.TotalMilliseconds);

        return offsets;
    }

    // Refresh always runs for whatever this scan actually saw (a listing
    // that came back is trustworthy fresh data regardless of whether the
    // page was truncated). Pruning only fires for an unfilled page (see
    // MaxListingsPerPage) — a truncated one can't tell us anything reliable
    // was actually removed, since a missing listing there might just be
    // sitting past the cutoff rather than gone. PF itself can only ever
    // show your own data center, so both are scoped to that DC's matches
    // only (see AlertPoller.RefreshFromScan/PruneMissing).
    private void SyncWithScan()
    {
        if (_listingsSinceRequest.Count == 0)
            return;

        var localDataCenter = MatchListView.GetLocalDataCenter();
        if (localDataCenter == null)
            return;

        _alertPoller.RefreshFromScan(_listingsSinceRequest, localDataCenter);

        if (_listingsSeenSinceRequest >= MaxListingsPerPage)
            return;

        if (!ExpectedRawCategories.TryGetValue(_pendingCategoryLabel, out var expected))
            return;

        // Deliberately NOT widened with whatever raw categories happened to
        // show up in _listingsSinceRequest — there's no dedicated PF tab for
        // e.g. "BlueMage" (a BLU-farmed Extreme trial's raw category is
        // "Trials", just reclassified to the BlueMage bucket for display),
        // so it only ever appears incidentally while scanning some other
        // category. Treating an incidental sighting as "this scan
        // comprehensively covered that whole category" caused real false
        // prunes — and false expire reports to the server — for listings
        // that were still genuinely active, just not part of what this
        // particular request actually walked. expected (the static per-
        // request guess) is the only thing this scan can honestly vouch for
        // completeness on; worst case for anything else is a stale listing
        // lingers a little longer, which is the safe failure mode.
        var categories = new HashSet<string>(expected);

        var seenIds = _listingsSinceRequest.Select(l => l.ListingId).ToHashSet();
        var removedListingIds = _alertPoller.PruneMissing(categories, localDataCenter, seenIds);

        // Tell the server too, so this doesn't just disappear from this
        // one client's own list — everyone else's poll (and the website)
        // still sees it as active until something reports otherwise (see
        // ListingUploader.ExpireAsync). Fire-and-forget: Tick() runs every
        // frame and can't block on the round trip.
        if (removedListingIds.Count > 0)
            _ = _uploader.ExpireAsync(removedListingIds);
    }

    private void RecordScan(DateTime at, string command, int count, string? firstListingName)
    {
        _history.Insert(0, new ScanResult(at, command, count, firstListingName));
        if (_history.Count > MaxHistoryEntries)
            _history.RemoveAt(_history.Count - 1);
    }

    // Count is however many ReceiveListing events actually fired between
    // this request and the sample delay elapsing.
    public readonly record struct ScanResult(DateTime At, string Command, int Count, string? FirstListingName);
}
