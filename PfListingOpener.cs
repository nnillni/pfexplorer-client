using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using PfExplorer.Models;
using PfExplorer.Windows;

namespace PfExplorer;

// Opens a specific PF listing in-game when a result is clicked — one click,
// one native call, never more (see Open's own doc comment for the exact
// priority order). AgentLookingForGroup.OpenListing(id) alone only shows
// something if the local client already has that listing's raw data cached,
// which is only true for listings your own client has actually requested —
// most results here came from someone else's plugin/xivpf, so a single
// click often can't reach a join popup in one step. Rather than chaining
// Show + RequestCategoryListings + OpenListing together behind one click
// (crosses from "UI convenience" into automating what should be several
// separate manual actions), each click does exactly the next thing that's
// missing, in priority order — repeated/idle clicks naturally walk the rest
// of the way there, and past that point get spent sweeping other
// categories' listings into the capture/upload pipeline instead of doing
// nothing.
public static class PfListingOpener
{
    // Party Finder itself is unreachable while logged out, inside a duty
    // instance, or between zones/cutscenes — RequestCategoryListings still
    // "succeeds" in those states, it just never yields ReceiveListing
    // calls. Shared by Open (below) and MatchListView's own travel-icon
    // gating — one definition of "actually in the open world right now"
    // instead of two that could silently drift apart.
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

    // Raw category (PfListingSearchResult.Category) -> the byte
    // RequestCategoryListings expects, needed by Open (below) to prefetch a
    // specific listing's category before jumping to it. NOT DutyCategory's
    // own bitflag enum value — the game's plain 1-based tab order instead
    // (live-confirmed: index 4 = Trials).
    private static readonly Dictionary<string, byte> RawCategoryToRequestByte = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Roulette"] = 1,
        ["Dungeons"] = 2,
        ["GuildQuests"] = 3,
        ["Trials"] = 4,
        ["Raids"] = 5,
        ["HighEndDuty"] = 6,
        ["PvP"] = 7,
        ["GoldSaucer"] = 8,
        ["FATEs"] = 9,
        ["TreasureHunts"] = 10,
        ["TheHunt"] = 11,
        ["GatheringForays"] = 12,
        ["DeepDungeons"] = 13,
        ["FieldOperations"] = 14,
        ["OccultCrescent"] = 14,
        ["VCDungeonFinder"] = 15,
        ["Other"] = 16,
    };

    public static byte? CategoryByteFor(string rawCategory) =>
        RawCategoryToRequestByte.TryGetValue(rawCategory, out var value) ? value : null;

    // For Open's tab-rotation fallback (see its own doc comment) — not
    // seeded/deterministic on purpose, this only ever needs to pick
    // "some other tab," never to be reproducible.
    private static readonly Random TabRandom = new();

    public static unsafe void Open(PfListingSearchResult listing)
    {
        // Opening the native PF window isn't meaningful mid-duty/zoning/
        // cutscene either — see IsInOpenWorld's own doc comment.
        if (!IsInOpenWorld)
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
            // No more one-click auto-travel — it let a click silently fire a
            // real world visit through whatever "/li"-bound plugin you had
            // installed, with no confirmation of its own beyond the popup
            // this used to show. Same region-gate as MatchListView's travel
            // icon uses for its tooltip — the World Visit System only allows
            // cross-DC travel within your own region (NA/EU/JP), except
            // Oceania, which is exempt from that restriction in both
            // directions — just to pick which toast to show, since neither
            // case does anything but tell you why you can't see this party.
            var localRegion = DataCenterRegions.RegionOf(localDataCenter);
            var targetRegion = DataCenterRegions.RegionOf(listing.DataCenter);
            var canTravel = targetRegion == "OCE" || (localRegion != null && localRegion == targetRegion);

            Plugin.ToastGui.ShowError(canTravel
                ? $"Needs to travel to {listing.DataCenter} to see this party — travel there yourself first."
                : $"Cannot travel to {listing.DataCenter} to see this party — different region, and only Oceania allows cross-region travel.");

            return;
        }

        var agent = AgentLookingForGroup.Instance();
        if (agent == null)
            return;

        // One click, one native call — checked fresh every click, in this
        // priority order:
        //   1. PF window not open yet? Open it. agent->Show() confirmed
        //      live to close the detail popup instead of opening the list
        //      window when called while the popup's already up (some
        //      native toggle-like behavior in AgentLookingForGroup's own
        //      Show(), not anything in our code) — ordering the window
        //      before the popup means Show() only ever runs while the
        //      popup definitely isn't open yet, so there's nothing for it
        //      to close.
        //   2. Window's open, but the join popup either isn't up yet or is
        //      showing a *different* listing than the one just clicked?
        //      Open this one. IsAddonVisible alone can't tell those two
        //      cases apart (it only knows some detail popup is up, not
        //      whose) — _lastOpenedListingId is what catches "you clicked
        //      someone else's listing" so that doesn't get mistaken for
        //      "already got what you asked for" and fall through to the
        //      tab-rotation step below instead of actually opening it.
        //   3. Both open AND it's already this exact listing showing?
        //      Nothing left worth doing for THIS listing — rather than a
        //      no-op click, sweep to a random other tab instead. Not
        //      user-facing usefulness; purely opportunistic, so repeated/
        //      idle clicking still ends up feeding more categories'
        //      listings into the capture/upload pipeline instead of doing
        //      nothing.
        // IsVisible (AtkUnitBase, inherited by every addon struct), not
        // just GetAddonByName returning non-null — that only means an
        // addon is loaded in memory, not that it's actually on screen.
        var lfgOpen = IsAddonVisible("LookingForGroup");
        var detailOpen = IsAddonVisible("LookingForGroupDetail");
        var isSameListing = listing.ListingId == _lastOpenedListingId;
        _lastOpenedListingId = listing.ListingId;

        if (!lfgOpen)
        {
            agent->Show();
            return;
        }

        if (!detailOpen || !isSameListing)
        {
            if (ulong.TryParse(listing.ListingId, out var listingId))
                agent->OpenListing(listingId);
            return;
        }

        // RequestCategoryListings alone refreshes the underlying list data
        // but doesn't reliably move the addon's own visible tab highlight —
        // pinning CategoryTab afterward is what actually makes the tab
        // selection itself visibly change instead of just the results.
        var tab = RandomOtherTab(agent->CategoryTab);
        agent->RequestCategoryListings(tab);
        agent->CategoryTab = tab;
    }

    // Tracks which listing the join popup (if any) actually belongs to —
    // see Open's own doc comment on why IsAddonVisible alone can't
    // distinguish "showing this listing" from "showing whatever was last
    // opened."
    private static string? _lastOpenedListingId;

    // GetAddonByName only tells you an addon is loaded, not that it's
    // actually being drawn — an addon can sit loaded-but-hidden (see Open's
    // own comment on why this matters). IsVisible (AtkUnitBase, inherited
    // by every addon struct) is the real check.
    private static unsafe bool IsAddonVisible(string name)
    {
        var addonPtr = Plugin.GameGui.GetAddonByName(name, 1).Address;
        return addonPtr != IntPtr.Zero && ((AtkUnitBase*)addonPtr)->IsVisible;
    }

    // Picks a real PF tab (1-16, see RawCategoryToRequestByte) other than
    // `current` — excluding it guarantees Open's tab-rotation fallback
    // (above) actually moves somewhere new on every one of these clicks
    // instead of occasionally re-picking the tab already showing by chance.
    private static byte RandomOtherTab(byte current)
    {
        byte next;
        do
        {
            next = (byte)TabRandom.Next(1, 17);
        } while (next == current);
        return next;
    }

    // Unlike LookingForGroup, there's no dedicated AgentLookingForGroup-style
    // wrapper for the Blue Magic Spellbook in ClientStructs — "/bluespellbook"
    // is a native game text command, not a Dalamud-registered one, so
    // ProcessCommand (which only dispatches to plugin-registered commands
    // like "/li") silently no-ops on it. Going through AgentModule directly
    // is the same approach every other addon-backed agent without its own
    // wrapper uses.
    public static unsafe void OpenBlueMageSpellbook()
    {
        if (Plugin.GameGui.GetAddonByName("AOZNotebook", 1).Address != IntPtr.Zero)
            return;

        var agent = Framework.Instance()->GetUIModule()->GetAgentModule()->GetAgentByInternalId(AgentId.AozNotebook);
        if (agent != null)
            agent->Show();
    }
}
