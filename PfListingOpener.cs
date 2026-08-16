using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface.Utility;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using PfExplorer.Models;
using PfExplorer.Windows;

namespace PfExplorer;

// Opens a specific PF listing in-game when a result is clicked. Doing this
// right takes more than just AgentLookingForGroup.OpenListing(id): that call
// only shows something if the local client already has that listing's raw
// data cached, which is only true for listings your own client has actually
// requested — most results here came from someone else's plugin/xivpf, so
// without a fresh RequestCategoryListings first, OpenListing would silently
// show nothing. Firing that request also happens to feed our own capture/
// upload pipeline, so a click here doubles as "go look at this" and "go
// capture this category" at the same time.
public static class PfListingOpener
{
    // Party Finder itself is unreachable while logged out, inside a duty
    // instance, or between zones/cutscenes — RequestCategoryListings still
    // "succeeds" in those states, it just never yields ReceiveListing
    // calls. Shared by Open/RequestTravel (below) and MatchListView's own
    // travel-button gating — one definition of "actually in the open world
    // right now" instead of two that could silently drift apart.
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

    // Fired right after Open (below) fires a RequestCategoryListings for a
    // clicked listing — wired up in Plugin's constructor to
    // PfBackgroundScraper.RecordManualRequest, so a click-triggered request
    // shows up in the Debug tab's scan log the same way a background scan
    // cycle step does, instead of being invisible there. A plain static
    // event rather than a direct PfBackgroundScraper reference — this class
    // is fully static and has no instance of it to hold, same reasoning as
    // every other cross-class hookup in Plugin's constructor.
    public static event Action<byte, string>? OnCategoryRequested;

    // Fired from CheckPendingDetailOpen (below) when a click's OpenListing
    // never actually produced a detail popup — wired up in Plugin's
    // constructor to AlertPoller.RemoveListingLocally, for the same reason
    // OnCategoryRequested exists: this class is static and holds no
    // AlertPoller reference of its own.
    public static event Action<string>? OnListingOpenFailed;

    // "/li" turned out to be bound to a travel plugin that executes a world
    // visit immediately, with no confirmation of its own — not something
    // to fire straight from a click, since it's a much bigger deal than
    // opening a window. DrawTravelConfirmation (wired into Plugin's Draw
    // hook) renders an actual Yes/No popup instead; this just records what
    // it's asking about and that the popup needs to open.
    private static (string DataCenter, string World)? _pendingTravel;
    private static bool _travelPopupNeedsOpen;
    private const string TravelPopupId = "Travel?##pf-travel-confirm";

    // OpenListing has no return value — the only way to tell whether it
    // actually produced a detail popup is to check back a moment later.
    // Set at the end of Open (below) whenever it fires OpenListing for a
    // category it just re-requested; CheckPendingDetailOpen (wired into
    // Plugin's Draw hook, same as DrawTravelConfirmation) samples it once
    // this elapses.
    private static string? _pendingDetailListingId;
    private static byte? _pendingDetailCategory;
    private static string? _pendingDetailCategoryLabel;
    private static DateTime? _pendingDetailCheckAt;
    // 600ms turned out too tight — under any real latency (RequestCategoryListings
    // itself is a round trip, and OpenListing presumably needs that data to
    // have actually landed first) it read as "failed" for a listing that
    // was really just slow, which used to be expensive: see
    // AlertPoller.RemoveListingLocally's own doc comment on why a false
    // positive here no longer costs a 10-minute poll suppression, but it's
    // still better not to false-positive in the first place. Matched to
    // PfBackgroundScraper.ResultSampleDelay, the same kind of "give the
    // game a moment to actually respond" wait used elsewhere in this file.
    private static readonly TimeSpan DetailCheckDelay = TimeSpan.FromSeconds(1.5);

    // Shared by Open (below, for a same-region-different-DC listing) and
    // MatchListView's dedicated Travel button — one confirmation flow
    // instead of two, so both surfaces behave the same way instead of one
    // asking and the other still firing instantly.
    public static void RequestTravel(string dataCenter, string world)
    {
        // The World Visit System (and whatever "/li" is bound to) isn't
        // usable mid-duty/zoning/cutscene any more than the PF window
        // itself is — see IsInOpenWorld above. Checked here too, not just
        // in Open below, since MatchListView's Travel button calls this
        // directly without going through Open.
        if (!IsInOpenWorld)
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

        // Deliberately no agent->Show() here — that opens the full list
        // window, which isn't what a listing click is for. RequestCategoryListings
        // alone is enough to warm the agent's cache for this listing's
        // category (background traffic only, same call the scraper fires,
        // just for one specific category instead of a walk through all of
        // them) so OpenListing below has real data to show, without ever
        // making the list itself visible.
        //
        // Prefers the display bucket over the raw category — MatchCategorizer
        // reclassifies specific fights (e.g. "The Unmaking (Extreme)") from
        // their raw "Trials"/"Raids" category into "HighEndDuty", which IS a
        // real PF tab; using the raw category here would silently request
        // the wrong tab for those. Falls back to the raw category when the
        // bucket isn't a real tab at all (e.g. "BlueMage", which is a
        // display-only grouping over ordinary Dungeons/Trials/Raids content,
        // not its own PF category).
        var categoryLabel = CategoryByteFor(MatchCategorizer.CategoryBucket(listing)) != null
            ? MatchCategorizer.CategoryBucket(listing)
            : listing.Category;
        var category = CategoryByteFor(categoryLabel);
        if (category is { } value)
        {
            agent->RequestCategoryListings(value);
            // Lets this show up in the Debug tab's scan log alongside
            // PfBackgroundScraper's own scheduled requests instead of being
            // invisible there — see OnCategoryRequested's own doc comment.
            OnCategoryRequested?.Invoke(value, categoryLabel);
        }

        // OpenListing is expected to pop just the detail popup on its own,
        // without needing the list window open/visible first, as long as
        // the agent already has this listing's data cached (which the
        // RequestCategoryListings call above just ensured) — not yet
        // live-confirmed with Show() removed, unlike most other native
        // agent behavior in this file.
        if (ulong.TryParse(listing.ListingId, out var listingId))
        {
            agent->OpenListing(listingId);

            // Scheduled regardless of whether `category` resolved to a real
            // tab above — CheckPendingDetailOpen still confirms/denies the
            // popup either way; it just skips the background re-scan step
            // if there's no category byte to request.
            _pendingDetailListingId = listing.ListingId;
            _pendingDetailCategory = category;
            _pendingDetailCategoryLabel = categoryLabel;
            _pendingDetailCheckAt = DateTime.UtcNow + DetailCheckDelay;
        }

        // Not just a visible-tab cosmetic (there's no list window open to
        // show it in anymore) — PfScanTracker.Tick() reads CategoryTab as
        // its own ground truth for "which category did the request that
        // just settled actually target," to decide which of Matches a
        // resulting unfilled page is allowed to prune. OpenListing appears
        // to overwrite CategoryTab from the listing's own data once it
        // resolves, which raced against RequestCategoryListings setting it
        // above — pinning it here, last, is what keeps it actually matching
        // the category this click just requested. Leaving this out (as a
        // previous version of this method did) meant a click's response
        // got attributed to whatever category CategoryTab last happened to
        // hold — often a stale, unrelated one — and PfScanTracker would
        // then prune that WRONG category's listings as "missing," which is
        // exactly the "results disappearing when another request happens"
        // bug this fixes.
        if (category is { } tab)
            agent->CategoryTab = tab;
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

    // GetAddonByName only tells you an addon is loaded, not that it's
    // actually being drawn — an addon can sit loaded-but-hidden. IsVisible
    // (AtkUnitBase, inherited by every addon struct) is the real check.
    private static unsafe bool IsAddonVisible(string name)
    {
        var addonPtr = Plugin.GameGui.GetAddonByName(name, 1).Address;
        return addonPtr != IntPtr.Zero && ((AtkUnitBase*)addonPtr)->IsVisible;
    }

    // Wired into Plugin's Draw hook alongside DrawTravelConfirmation —
    // samples whether Open's own OpenListing call (DetailCheckDelay ago)
    // actually produced a detail popup. A miss here almost always means the
    // listing is already gone — someone else joined/closed it, or it aged
    // out, between whenever the alert list last saw it and this click — so
    // this both fixes the results list immediately (RemoveListingLocally,
    // via OnListingOpenFailed) instead of leaving a dead entry sitting
    // there until the next poll, and fires a background re-request for the
    // category so PfBackgroundScraper's own scan/prune independently
    // confirms it, same as any other capture.
    public static unsafe void CheckPendingDetailOpen()
    {
        if (_pendingDetailCheckAt is not { } checkAt || DateTime.UtcNow < checkAt)
            return;

        var listingId = _pendingDetailListingId;
        var category = _pendingDetailCategory;
        var categoryLabel = _pendingDetailCategoryLabel;
        _pendingDetailCheckAt = null;
        _pendingDetailListingId = null;
        _pendingDetailCategory = null;
        _pendingDetailCategoryLabel = null;

        if (IsAddonVisible("LookingForGroupDetail"))
            return;

        if (listingId != null)
            OnListingOpenFailed?.Invoke(listingId);

        if (category is not { } value || categoryLabel == null)
            return;

        var agent = AgentLookingForGroup.Instance();
        if (agent == null)
            return;

        agent->RequestCategoryListings(value);
        OnCategoryRequested?.Invoke(value, categoryLabel);
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
            ImGui.SetNextWindowPos(new Vector2(
                clickPos.X + 12 * ImGuiHelpers.GlobalScale,
                clickPos.Y + 8 * ImGuiHelpers.GlobalScale));
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
                // ProcessCommand only dispatches to plugin-registered
                // commands (see OpenBlueMageSpellbook's comment above) and
                // returns false silently if nothing owns "/li" — unlike
                // actually typing it in the chat box, nothing shows up on
                // screen in that case. Surface that ourselves so "no travel
                // plugin installed" looks like an error instead of a dead
                // click.
                if (!Plugin.CommandManager.ProcessCommand($"/li {pending.DataCenter}"))
                {
                    Plugin.ChatGui.PrintError("/li command not found.");
                }

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
