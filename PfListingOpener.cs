using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using PfExplorer.Models;
using PfExplorer.Windows;

namespace PfExplorer;

// Opens a specific PF listing in-game when a result is clicked. Doing this
// right takes more than just AgentLookingForGroup.OpenListing(id): that call
// only shows something if the local client already has that listing's raw
// data cached, which is only true for listings your own client has actually
// requested — most results here came from someone else's plugin/xivpf, so
// without a fresh RequestCategoryListings first, OpenListing would silently
// show nothing. Firing that request also happens to be exactly what feeds
// our own capture/upload pipeline (see PfBackgroundScraper's own comment on
// RequestCategoryListings), so a click here doubles as "go look at this"
// and "go capture this category" at the same time.
public static class PfListingOpener
{
    // "/li" turned out to be bound to a travel plugin (e.g. Lifestream) that
    // executes a world visit immediately, with no confirmation of its own —
    // not something to fire straight from a click, since it's a much bigger
    // deal than opening a window. DrawTravelConfirmation (wired into
    // Plugin's Draw hook) renders an actual Yes/No popup instead; this just
    // records what it's asking about and that the popup needs to open.
    private static (string DataCenter, string World)? _pendingTravel;
    private static bool _travelPopupNeedsOpen;
    private const string TravelPopupId = "Travel?##pf-travel-confirm";

    // Shared by Open (below, for a same-region-different-DC listing) and
    // MatchListView's dedicated Travel button — one confirmation flow
    // instead of two, so both surfaces behave the same way instead of one
    // asking and the other still firing instantly.
    public static void RequestTravel(string dataCenter, string world)
    {
        // The World Visit System (and whatever "/li" is bound to) isn't
        // usable mid-duty/zoning/cutscene any more than the PF window
        // itself is — see PfBackgroundScraper.IsInOpenWorld. Checked here
        // too, not just in Open below, since MatchListView's Travel button
        // calls this directly without going through Open.
        if (!PfBackgroundScraper.IsInOpenWorld)
        {
            Plugin.ToastGui.ShowError("Can't travel right now — only available out in the open world.");
            return;
        }

        _pendingTravel = (dataCenter, world);
        _travelPopupNeedsOpen = true;
    }

    public static unsafe void Open(PfListingSearchResult listing)
    {
        // Same reasoning as RequestTravel above — opening the native PF
        // window isn't meaningful mid-duty/zoning/cutscene either.
        if (!PfBackgroundScraper.IsInOpenWorld)
        {
            Plugin.ToastGui.ShowError("Can't open Party Finder right now — only available out in the open world.");
            return;
        }

        // Checked before touching the agent at all — Party Finder only ever
        // shows your own data center's listings, so there's nothing useful
        // to open here. Not opening the window at all (rather than opening
        // it and stopping) is deliberate: if you're already in a duty or
        // otherwise not looking at PF, there's no reason to pop it open
        // just to show an error/travel prompt you'd see either way.
        var localDataCenter = MatchListView.GetLocalDataCenter();
        if (localDataCenter != null && !string.Equals(localDataCenter, listing.DataCenter, StringComparison.OrdinalIgnoreCase))
        {
            // Same region-gate as MatchListView's dedicated Travel button —
            // the World Visit System only allows cross-DC travel within your
            // own region (NA/EU/JP), except Oceania, which is exempt from
            // that restriction in both directions.
            var localRegion = DataCenterRegions.RegionOf(localDataCenter);
            var targetRegion = DataCenterRegions.RegionOf(listing.DataCenter);
            var canTravel = targetRegion == "OCE" || (localRegion != null && localRegion == targetRegion);

            if (canTravel)
            {
                RequestTravel(listing.DataCenter, listing.World);
            }
            else
            {
                Plugin.ToastGui.ShowError($"Can't travel to {listing.DataCenter} to see this party — different region, and only Oceania allows cross-region travel.");
            }

            return;
        }

        var agent = AgentLookingForGroup.Instance();
        if (agent == null)
            return;

        // Only Show() if it isn't already open — same "LookingForGroup"
        // addon check PfBackgroundScraper uses to avoid disturbing an
        // active browse. Calling Show() again on an already-open window
        // isn't needed for anything below, so skip it rather than risk
        // resetting scroll/layout state for no reason.
        if (Plugin.GameGui.GetAddonByName("LookingForGroup", 1).Address == IntPtr.Zero)
            agent->Show();

        // Always fires, open-or-not: this is what actually switches to the
        // right tab (a different category from whatever was showing) or
        // refreshes it (already on that tab) — RequestCategoryListings
        // re-requests either way, there's no separate "just switch" vs
        // "just refresh" case to distinguish here.
        var category = PfBackgroundScraper.CategoryByteFor(listing.Category);
        if (category is { } value)
            agent->RequestCategoryListings(value);

        if (ulong.TryParse(listing.ListingId, out var listingId))
            agent->OpenListing(listingId);

        // OpenListing appears to set CategoryTab from the listing's own data
        // once it resolves, which can clobber what RequestCategoryListings
        // just set — pinning it here, last, is what actually keeps the
        // visible tab matching the category we requested regardless of
        // whatever order those two do their own internal bookkeeping in.
        if (category is { } tab)
            agent->CategoryTab = tab;
    }

    // Wired into Plugin's Draw hook, runs every frame regardless of
    // _pendingTravel's state — ImGui.OpenPopup has to be called from inside
    // an active frame, which a chat link's click callback isn't guaranteed
    // to be, so this is the one place that's actually safe to call it from
    // (once, on the frame right after Open sets a pending request).
    public static void DrawTravelConfirmation()
    {
        if (_travelPopupNeedsOpen)
        {
            // Captured here rather than at the actual click (RequestTravel/
            // Open) — this runs on the very next Draw after that click, in
            // the same frame in the row-click case, so the mouse hasn't
            // meaningfully moved, and this is guaranteed to run inside an
            // active ImGui frame (see this method's own doc comment) where
            // a chat link's click callback isn't. Offset right/down a
            // little so the popup doesn't open directly under the cursor.
            var clickPos = ImGui.GetMousePos();
            ImGui.SetNextWindowPos(new Vector2(clickPos.X + 12, clickPos.Y + 8));
            ImGui.OpenPopup(TravelPopupId);
            _travelPopupNeedsOpen = false;
        }

        if (_pendingTravel is not { } pending)
            return;

        // A regular (non-modal) popup, not BeginPopupModal — a modal centers
        // itself and dims the whole screen behind it regardless of
        // SetNextWindowPos, which read as "fullscreen" for something this
        // small; a plain popup stays where it's placed, has no dimming, and
        // closes on its own if you click elsewhere instead of needing an
        // explicit Cancel.
        if (ImGui.BeginPopup(TravelPopupId, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextUnformatted($"Travel to {pending.World} ({pending.DataCenter}) to see this party?");
            ImGui.Spacing();

            if (ImGui.Button("Travel"))
            {
                Plugin.CommandManager.ProcessCommand($"/li {pending.DataCenter}");
                _pendingTravel = null;
                ImGui.CloseCurrentPopup();
            }

            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
            {
                _pendingTravel = null;
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }
    }
}
