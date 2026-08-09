using System;
using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using PfExplorer.Models;
using PfExplorer.Windows;

namespace PfExplorer;

// Turns the raw stream of ReceiveListing events your own organic Party
// Finder browsing generates (opening the window, switching a category tab,
// or PfListingOpener prefetching a category before jumping to a listing)
// into the same "was this a complete, unfilled page" signal
// PfBackgroundScraper used to derive from its own timed requests — but
// purely reactive: this never calls RequestCategoryListings or anything
// else that would generate game traffic on its own. It only reads what the
// game already handed to us because you clicked something.
public sealed class PfScanTracker
{
    // The game caps a single RequestCategoryListings response at 50
    // listings — a batch that comes back under that isn't truncated, so
    // it's the full, current set for whichever category tab was active
    // rather than just the first slice of a longer one. Only an unfilled
    // batch like that is safe to treat as "if it's not in here, it's gone"
    // for pruning.
    private const int MaxListingsPerPage = 50;

    // How long to wait after the last ReceiveListing event before treating
    // the batch as settled. The game fires these back-to-back in a burst as
    // a category page populates; this just needs to be comfortably longer
    // than the gap between events within one burst, and comfortably shorter
    // than the gap between two separate browsing actions.
    private static readonly TimeSpan DebounceWindow = TimeSpan.FromMilliseconds(800);

    // Byte RequestCategoryListings expects -> the MatchCategorizer display
    // bucket(s) a page for that tab is expected to contain — NOT the raw
    // PfListingDto.Category value. They're the same string for most tabs,
    // but not High End Duty: specific fights get reclassified into the
    // "HighEndDuty" bucket by duty name alone (see
    // MatchCategorizer.HighEndDutyNames), while their raw Category field
    // still reads "Trials"/"Raids" straight from the game. Matching on the
    // raw field here would mean an unfilled High End Duty scan never prunes
    // anything (nothing in Matches literally carries Category=="HighEndDuty"),
    // leaving stale entries (e.g. from xivpf.com) sitting there forever even
    // after your own client proves they're gone — see AlertPoller.
    // PruneMissing's own doc comment. FieldOperations/OccultCrescent share
    // one tab (14) since the game itself combines them into a single
    // category list, both capped by the same 50-listing page. Deliberately
    // NOT inferred from whatever buckets happen to show up in the batch
    // itself — a wrong/incomplete guess here just means pruning silently
    // no-ops for that bucket, not a bad prune.
    private static readonly Dictionary<byte, string[]> ExpectedBucketsByTab = new()
    {
        [1] = new[] { "Roulette" },
        [2] = new[] { "Dungeons" },
        [3] = new[] { "GuildQuests" },
        [4] = new[] { "Trials" },
        [5] = new[] { "Raids" },
        [6] = new[] { "HighEndDuty" },
        [7] = new[] { "PvP" },
        [8] = new[] { "GoldSaucer" },
        [9] = new[] { "FATEs" },
        [10] = new[] { "TreasureHunts" },
        [11] = new[] { "TheHunt" },
        [12] = new[] { "GatheringForays" },
        [13] = new[] { "DeepDungeons" },
        [14] = new[] { "FieldOperations", "OccultCrescent" },
        [15] = new[] { "VCDungeonFinder" },
        [16] = new[] { "Other" },
    };

    private readonly AlertPoller _alertPoller;
    private readonly ListingUploader _uploader;

    private readonly List<PfListingDto> _pending = new();
    private DateTime? _lastReceivedAt;

    public PfScanTracker(AlertPoller alertPoller, ListingUploader uploader)
    {
        _alertPoller = alertPoller;
        _uploader = uploader;
    }

    // Called from Plugin.OnReceiveListing for every listing the game hands
    // back, regardless of source — the only thing that ever adds to the
    // pending batch, so this class never sees a listing it wasn't already
    // being fed anyway.
    public void NotifyListingReceived(PfListingDto dto)
    {
        _pending.Add(dto);
        _lastReceivedAt = DateTime.UtcNow;
    }

    // Pumped from Plugin's Draw hook every frame — cheap when idle (a date
    // check), and never issues a game request itself; it only ever reads
    // AgentLookingForGroup.CategoryTab to find out which tab you were
    // already looking at.
    public unsafe void Tick()
    {
        if (_pending.Count == 0 || _lastReceivedAt is not { } lastReceivedAt)
            return;

        if (DateTime.UtcNow - lastReceivedAt < DebounceWindow)
            return;

        var batch = _pending.ToList();
        _pending.Clear();
        _lastReceivedAt = null;

        var localDataCenter = MatchListView.GetLocalDataCenter();
        if (localDataCenter == null)
            return;

        if (batch.Count >= MaxListingsPerPage)
            return;

        var agent = AgentLookingForGroup.Instance();
        if (agent == null)
            return;
        if (!ExpectedBucketsByTab.TryGetValue(agent->CategoryTab, out var expected))
            return;

        var buckets = new HashSet<string>(expected);
        var seenIds = batch.Select(l => l.ListingId).ToHashSet();
        var removedListingIds = _alertPoller.PruneMissing(buckets, localDataCenter, seenIds);

        // Tell the server too, so this doesn't just disappear from this one
        // client's own list — everyone else's poll (and the website) still
        // sees it as active until something reports otherwise.
        // Fire-and-forget: Tick() runs every frame and can't block on the
        // round trip.
        if (removedListingIds.Count > 0)
            _ = _uploader.ExpireAsync(removedListingIds);
    }
}
