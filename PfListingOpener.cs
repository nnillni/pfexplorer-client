using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace PfExplorer;

// Jumps straight to a specific PF listing's detail popup in-game via the
// game's own native mechanism — the same call it uses internally (e.g. from
// a player context menu). Doesn't trigger a real category search itself
// (see PfBackgroundScraper for the thing that keeps broader capture data
// flowing to the server instead).
public static class PfListingOpener
{
    public static void Open(string listingIdText)
    {
        if (ulong.TryParse(listingIdText, out var listingId))
            OpenDirect(listingId);
    }

    private static unsafe void OpenDirect(ulong listingId)
    {
        var agent = AgentLookingForGroup.Instance();
        if (agent == null)
            return;

        agent->OpenListing(listingId);
    }
}
