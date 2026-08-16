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
// Pauses while the native PF window is open AND being actively used —
// firing RequestCategoryListings resets CategoryTab, which would yank the
// category/page out from under you mid-browse. "Actively used" is idle-
// tracked (see Tick's window-open branch/UpdateWindowActivity), not just
// "window exists": leaving it open untouched for WindowIdleThreshold lets
// scanning resume anyway, and switching tabs yourself while it's open gets
// captured the same way a scan would (see RecordManualRequest's "Tab"
// source) instead of only ever happening once the window closes.
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

    // Tripled while the player's own online status reads AFK — no point
    // walking every category at the normal, request-dense cadence just to
    // keep the server fresh for a client that isn't actually being played
    // right now. Picked up once per cycle (see Tick's cycle-start branch),
    // not re-evaluated mid-cycle: going AFK partway through a cycle just
    // means the NEXT cycle stretches out, not the current one changing
    // shape underneath itself.
    private static readonly TimeSpan CycleWindowAfk = TimeSpan.FromMinutes(6);

    // FFXIV's OnlineStatus sheet row id for "Away from Keyboard" — the
    // status the game itself assigns after your own configured AFK timer
    // elapses (visible as the "zzz" icon over your character). Stable,
    // long-documented id, not something exposed as a named constant
    // anywhere in Dalamud/Lumina.
    private const uint AfkOnlineStatusId = 17;

    // False whenever LocalPlayer is unavailable (e.g. between zones) rather
    // than throwing — same defensive pattern as MatchListView.GetLocalDataCenter.
    private static bool IsPlayerAfk => Plugin.ObjectTable.LocalPlayer?.OnlineStatus.RowId == AfkOnlineStatusId;
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

    // How long the native PF window has to sit untouched — no tab or search
    // area switch — before scanning is allowed to resume despite the window
    // still being open. See Tick's own window-open branch for the full
    // reasoning; this only needs to be "long enough that a genuinely active
    // browse never sees a mid-browse category yank," not tuned any finer.
    private static readonly TimeSpan WindowIdleThreshold = TimeSpan.FromSeconds(30);

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

    // Idle-tracking state for the window-open branch of Tick() — see
    // UpdateWindowActivity's own doc comment. _windowActivityInitialized
    // resets to false whenever the window is observed closed, so a fresh
    // open later starts its own 30s countdown instead of inheriting
    // whatever CategoryTab/SearchAreaTab happened to be set from a much
    // earlier session.
    private bool _windowActivityInitialized;
    private byte _lastObservedCategoryTab;
    private byte _lastObservedSearchAreaTab;
    private DateTime _lastActivityAt = DateTime.UtcNow;

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

    // True from the moment a request (scheduled or RecordManualRequest)
    // fires until its result gets sampled ~1.5s later — Plugin checks this
    // before feeding a listing to PfScanTracker (see its own doc comment on
    // why): a listing arriving while this is true can't be reliably
    // attributed to organic browsing vs whatever request caused THIS class
    // to be waiting on a sample, and PfScanTracker's own CategoryTab-
    // snapshot approach has no way to tell the difference either.
    public bool HasPendingSample => _pendingResultCheckAt != null;

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
    // Party Finder itself is unreachable while logged out, inside a duty
    // instance, or between zones/cutscenes — RequestCategoryListings still
    // "succeeds" in those states, it just never yields ReceiveListing
    // calls. Shared with PfListingOpener, which uses the exact same
    // condition to gate manual open/travel clicks — one definition of
    // "actually in the open world right now" instead of two that could
    // silently drift apart.
    public static bool IsInOpenWorld =>
        Plugin.ClientState.IsLoggedIn
        && !Plugin.Condition[ConditionFlag.BoundByDuty]
        && !Plugin.Condition[ConditionFlag.BoundByDuty56]
        && !Plugin.Condition[ConditionFlag.BoundByDuty95]
        && !Plugin.Condition[ConditionFlag.BetweenAreas]
        && !Plugin.Condition[ConditionFlag.BetweenAreas51]
        && !Plugin.Condition[ConditionFlag.WatchingCutscene]
        && !Plugin.Condition[ConditionFlag.WatchingCutscene78]
        && !Plugin.Condition[ConditionFlag.OccupiedInCutSceneEvent];

    public unsafe void Tick()
    {
        var now = DateTime.UtcNow;

        // Processed unconditionally, ahead of the AlertBackgroundScraperEnabled/
        // IsInOpenWorld/window-open gates below — a manual click can call
        // RecordManualRequest even while the scheduled walk itself is off
        // (or mid-duty, where Open() never even fires the request in the
        // first place — see IsInOpenWorld), and its result still needs to
        // get sampled and logged rather than sitting pending forever.
        if (_pendingResultCheckAt is { } checkAt && now >= checkAt)
        {
            _pendingResultCheckAt = null;
            RecordScan(now, _pendingLabel, _listingsSeenSinceRequest, _firstListingSinceRequest);
            _lastKnownCount[_pendingCategoryLabel] = _listingsSeenSinceRequest;
            SyncWithScan();
        }

        if (!_config.AlertBackgroundScraperEnabled)
            return;

        if (!IsInOpenWorld)
            return;

        // The window being open no longer blocks scanning outright — only
        // actively touching it does. RequestCategoryListings resets
        // CategoryTab, which would yank the category/page out from under a
        // genuine active browse, but there's no reason to sit fully paused
        // for however long the window happens to stay open afterward if
        // it's just sitting there untouched (e.g. left open while AFK, or
        // parked on a tab while doing something else in-game). Once
        // WindowIdleThreshold has passed with no tab/search-area change,
        // scanning resumes even though the window is technically still
        // open; the very next real interaction re-pauses it immediately
        // (see UpdateWindowActivity).
        if (Plugin.GameGui.GetAddonByName("LookingForGroup", 1).Address != IntPtr.Zero)
        {
            UpdateWindowActivity(now);
            if (now - _lastActivityAt < WindowIdleThreshold)
                return;
        }
        else
        {
            // Window closed — nothing to track. Reset so a later open
            // starts its own idle countdown from scratch instead of
            // inheriting a stale timestamp/tab snapshot from this session.
            _windowActivityInitialized = false;
            _lastActivityAt = now;
        }

        if (_stepIndex >= Categories.Length)
        {
            if (now < _nextCycleAt)
                return;

            var cycleLength = JitteredDelay(IsPlayerAfk ? CycleWindowAfk : CycleWindow);
            _stepIndex = 0;
            _cycleStartAt = now;
            _stepOffsets = RandomStepOffsets(cycleLength, Categories.Length);
            _nextCycleAt = now + cycleLength;
            ShuffleStepOrder(_stepOrder);
        }

        if (now < _cycleStartAt + _stepOffsets[_stepIndex])
            return;

        // A click's own RecordManualRequest may already have a sample
        // pending — firing this scheduled step now would clobber it the
        // same way an unguarded RecordManualRequest could clobber a
        // scheduled step's own sample (see RecordManualRequest's own doc
        // comment). Don't advance _stepIndex either: this step just retries
        // next frame once whatever's pending clears, typically well under
        // ResultSampleDelay later.
        if (_pendingResultCheckAt != null)
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
            {
                requestAgent->RequestCategoryListings(category);
                // Pre-acknowledge the CategoryTab change this causes — this
                // step can fire while the window is open (idle-resumed, see
                // Tick's window-open branch), so without this the very next
                // UpdateWindowActivity call would see CategoryTab change and
                // mistake this step's own request for user activity,
                // immediately re-pausing right after resuming.
                _lastObservedCategoryTab = category;
            }
            else
            {
                requestAgent->RequestListingsUpdate();
            }
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

    // Wired up to PfListingOpener.OnCategoryRequested in Plugin's
    // constructor — lets a listing click's own RequestCategoryListings show
    // up in the same Debug tab scan log as a scheduled background step,
    // instead of being invisible there, by reusing the exact same pending-
    // sample/RecordScan machinery Tick's own scheduled steps use (the
    // request itself already happened in PfListingOpener.Open by the time
    // this is called — this only starts tracking its result).
    //
    // Shares the single _pending* fields with Tick's own scheduled steps
    // rather than tracking multiple in-flight requests independently — that
    // isn't actually possible here regardless, since a ReceiveListing event
    // carries no signal on which outstanding request caused it, only ever
    // "some request got some listings back." Because of that, this bails
    // out instead of overwriting an already-pending sample: a listing click
    // is common enough (unlike Tick's own scheduled steps, which space
    // themselves out over roughly a 2-minute cycle) that landing inside
    // another request's ~1.5s sample window isn't rare at all — clobbering
    // that request's _listingsSinceRequest mid-count previously meant
    // SyncWithScan saw an artificially small "complete" page for whatever
    // category the earlier request actually targeted, and pruned every
    // other listing in that category as falsely missing. Losing this one
    // click's own scan-log entry is a fair trade for not corrupting a
    // pruning decision that's already in flight.
    // `source` distinguishes a listing click ("Click", the default —
    // PfListingOpener.Open) from a genuine native tab switch ("Tab" — see
    // UpdateWindowActivity) in the resulting scan-log line; both share this
    // same method since both are "some category's data is about to arrive,
    // start tracking it" in exactly the same way.
    public void RecordManualRequest(byte category, string label, string source = "Click")
    {
        // Set unconditionally, even if bailing out below — CategoryTab
        // physically becomes `category` regardless of whether this class
        // has room to track its result right now (PfListingOpener.Open
        // already pinned it, or UpdateWindowActivity is reporting what it
        // just observed), so the idle-activity baseline needs to reflect
        // that either way or the next check would misread it as fresh
        // activity.
        _lastObservedCategoryTab = category;

        if (_pendingResultCheckAt != null)
            return;

        _pendingLabel = $"{source}(RequestCategoryListings({label}={category}))";
        _pendingCategoryLabel = label;
        _firstListingSinceRequest = null;
        _listingsSeenSinceRequest = 0;
        _listingsSinceRequest.Clear();
        _pendingResultCheckAt = DateTime.UtcNow + ResultSampleDelay;
    }

    // Called from Tick's window-open branch. Compares AgentLookingForGroup's
    // live CategoryTab/SearchAreaTab against what was last observed (or, if
    // we just caused the change ourselves — a scheduled step's own
    // RequestCategoryListings, or a click via RecordManualRequest — against
    // what we already pre-acknowledged) to tell genuine user navigation
    // apart from our own traffic, resetting the idle clock only for the
    // former.
    //
    // A CategoryTab change specifically also gets treated as "go capture
    // this category" — the user just switched to it themselves, so the game
    // is about to hand back exactly the same kind of data a scheduled/click
    // request would, for free; RecordManualRequest picks it up the same way
    // it does a click, complete with its own scan-log entry (tagged "Tab"
    // instead of "Click") and pruning pass.
    //
    // Known gap: opening a listing's detail popup from inside the native
    // window itself (as opposed to through this plugin's own results list)
    // isn't visible to us as a distinct event — only as whatever CategoryTab
    // ends up reading afterward. In the common case that's a no-op (the
    // listing's own category already matches whatever tab it was shown
    // under), so it won't false-trigger here; a listing whose category
    // doesn't match the tab it's displayed under (e.g. a HighEndDuty-
    // reclassified fight shown while the raw "Trials" tab is active) is the
    // one case this can't tell apart from a real tab switch. No native hook
    // exists to distinguish "opened a listing's detail" from "changed tabs"
    // any more precisely than that.
    private unsafe void UpdateWindowActivity(DateTime now)
    {
        var agent = AgentLookingForGroup.Instance();
        if (agent == null)
            return;

        if (!_windowActivityInitialized)
        {
            _windowActivityInitialized = true;
            _lastActivityAt = now;
            // Opening the window is itself worth capturing — whatever tab
            // it opened onto is about to show its own listings for free,
            // same as switching to it manually (see the change-detection
            // branch below), just without an actual "change" to detect
            // since there's no prior observation to compare against yet.
            CaptureCurrentTab(agent->CategoryTab, source: "Open");
            _lastObservedSearchAreaTab = agent->SearchAreaTab;
            return;
        }

        var categoryTabChanged = agent->CategoryTab != _lastObservedCategoryTab;
        var searchAreaChanged = agent->SearchAreaTab != _lastObservedSearchAreaTab;
        if (!categoryTabChanged && !searchAreaChanged)
            return;

        _lastActivityAt = now;
        _lastObservedSearchAreaTab = agent->SearchAreaTab;

        if (categoryTabChanged)
            CaptureCurrentTab(agent->CategoryTab, source: "Tab");
    }

    // Shared by UpdateWindowActivity's two call sites (window just opened,
    // or CategoryTab just changed) — captures whatever tab is now showing
    // via RecordManualRequest under a label matching LabelForCategoryTab,
    // falling back to an "Unknown(N)" label for a CategoryTab value none of
    // the 16 known categories map to (e.g. 0, seen briefly right as the
    // window opens or seemingly as a genuinely reachable state on its own —
    // not yet fully understood). RecordManualRequest still sets
    // _lastObservedCategoryTab and still runs RefreshFromScan/logs the scan
    // either way (see SyncWithScan) — only the category-specific pruning
    // pass gets skipped for an unrecognized value, since there's no known
    // raw category to safely prune against. That means results still
    // refresh/appear for an unrecognized tab; only the "prune what's
    // missing" half is deliberately withheld until it maps to something we
    // can vouch for.
    private void CaptureCurrentTab(byte categoryTab, string source) =>
        RecordManualRequest(categoryTab, LabelForCategoryTab(categoryTab) ?? $"Unknown({categoryTab})", source);

    private static string? LabelForCategoryTab(byte categoryTab)
    {
        foreach (var (categoryValue, label) in Categories)
        {
            if (categoryValue == categoryTab)
                return label;
        }

        return null;
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

    // Inverse of ExpectedRawCategories, built once and cached — maps a
    // listing's raw PfListingSearchResult.Category (e.g. "Dungeons",
    // "FieldOperations") back to the byte RequestCategoryListings expects.
    // Used by PfListingOpener to prefetch real listing data for a specific
    // category before jumping to a listing in it — the same request this
    // class fires for its own background walk, just triggered on demand
    // instead of on a schedule.
    private static readonly Lazy<Dictionary<string, byte>> RawCategoryToRequestByte = new(() =>
    {
        var map = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        foreach (var (categoryValue, label) in Categories)
        {
            if (categoryValue is not { } value)
                continue;
            if (!ExpectedRawCategories.TryGetValue(label, out var rawCategories))
                continue;
            foreach (var raw in rawCategories)
                map[raw] = value;
        }
        return map;
    });

    public static byte? CategoryByteFor(string rawCategory) =>
        RawCategoryToRequestByte.Value.TryGetValue(rawCategory, out var value) ? value : null;

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
