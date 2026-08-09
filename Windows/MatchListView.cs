using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using PfExplorer.Models;

namespace PfExplorer.Windows;

// The live match list (refresh button, category tabs, and the results
// table) — factored out of StatusWindow so both it and the standalone
// results-only MatchesWindow can share the same rendering, icon cache, and
// selected-category/local-DC state instead of duplicating it.
public class MatchListView : IDisposable
{
    // Same labels as the website's TAG_META — colors approximate its
    // gold/cyan/green/plain palette.
    private static readonly Vector4 GoldColor = new(0.85f, 0.72f, 0.47f, 1f);
    private static readonly Vector4 CyanColor = new(0.44f, 0.82f, 0.91f, 1f);
    private static readonly Vector4 GreenColor = new(0.44f, 0.75f, 0.55f, 1f);
    private static readonly Vector4 PlainColor = new(0.71f, 0.68f, 0.62f, 1f);

    // Role colors matching the website's --role-tank/--role-healer/--role-dps
    // (job-square coloring in website/app.js's JOB_META/splitRoleBackground).
    private static readonly Vector4 TankColor = new(0.31f, 0.56f, 0.84f, 1f);
    private static readonly Vector4 HealerColor = new(0.30f, 0.75f, 0.44f, 1f);
    private static readonly Vector4 DpsColor = new(0.85f, 0.33f, 0.30f, 1f);
    private static readonly Vector4 AnyRoleColor = new(0.57f, 0.60f, 0.67f, 1f);

    // Same job -> role grouping as the website's JOB_META.
    private static readonly Dictionary<string, Vector4> JobRoleColor = new()
    {
        ["PLD"] = TankColor, ["WAR"] = TankColor, ["DRK"] = TankColor, ["GNB"] = TankColor,
        ["WHM"] = HealerColor, ["SCH"] = HealerColor, ["AST"] = HealerColor, ["SGE"] = HealerColor,
        ["MNK"] = DpsColor, ["DRG"] = DpsColor, ["NIN"] = DpsColor, ["SAM"] = DpsColor, ["RPR"] = DpsColor, ["VPR"] = DpsColor,
        ["BRD"] = DpsColor, ["MCH"] = DpsColor, ["DNC"] = DpsColor,
        ["BLM"] = DpsColor, ["SMN"] = DpsColor, ["RDM"] = DpsColor, ["PCT"] = DpsColor, ["BLU"] = DpsColor,
    };

    private static readonly Dictionary<string, (string Label, Vector4 Color)> TagMeta = new()
    {
        ["DutyCompletion"] = ("Duty Completion", CyanColor),
        ["Practice"] = ("Practice", GreenColor),
        ["Loot"] = ("Loot", GoldColor),
        ["DutyComplete"] = ("Duty Complete", GoldColor),
        ["DutyIncomplete"] = ("Duty Incomplete", PlainColor),
        ["DutyCompleteWeeklyUnclaimed"] = ("Weekly Unclaimed", PlainColor),
        ["OnePlayerPerJob"] = ("One Player per Job", GoldColor),
    };

    // One icon file per bucket, mirroring the website's CATEGORY_ICONS
    // (website/app.js). Bucket names/order/labels themselves live in
    // MatchCategorizer, shared with AlertPoller's announcement text.
    private static readonly Dictionary<string, string> CategoryIconFiles = new()
    {
        ["Roulette"] = "roulette.png",
        ["Dungeons"] = "dungeon.png",
        ["GuildQuests"] = "guildhest.png",
        ["Trials"] = "trial.png",
        ["Raids"] = "raid.png",
        ["HighEndDuty"] = "ultimate.png",
        ["FATEs"] = "fate.png",
        ["TheHunt"] = "hunt.png",
        ["GoldSaucer"] = "goldsaucer.png",
        ["GatheringForays"] = "gathering.png",
        ["VCDungeonFinder"] = "variant.png",
        ["FieldOperations"] = "field-operations.png",
        ["OccultCrescent"] = "field-operations.png",
        ["QuestBattles"] = "questbattles.png",
        ["PvP"] = "pvp.png",
        ["TreasureHunts"] = "treasure.png",
        ["None"] = "roleplay.png",
        ["Other"] = "roleplay.png",
        ["BlueMage"] = "bluemage.png",
    };

    // Same rgba tints as the website's .listing.freshness-* classes
    // (website/style.css), at a slightly higher alpha since ImGui's table
    // row background sits under less other styling than the website's row.
    private static readonly Vector4 FreshnessGreenBg = new(111f / 255, 191f / 255, 139f / 255, 0.28f);
    private static readonly Vector4 FreshnessYellowBg = new(216f / 255, 184f / 255, 119f / 255, 0.28f);
    private static readonly Vector4 FreshnessRedBg = new(224f / 255, 104f / 255, 95f / 255, 0.28f);

    private static Vector4 FreshnessBg(int rank) => rank switch
    {
        0 => FreshnessGreenBg,
        1 => FreshnessYellowBg,
        _ => FreshnessRedBg,
    };

    // Same wording as the website's formatLastUpdated (app.js) — shown in
    // place of the data center (dropped in favor of this; the travel
    // button already makes the DC obvious) in the row's world/recruiter
    // cell. Includes seconds under 3 minutes (matching the green freshness
    // threshold) since "Updated 0 min ago" is meaningless right after a
    // capture but "Updated 1m 20s ago" isn't.
    private static string FormatLastUpdated(string capturedAt)
    {
        if (!DateTime.TryParse(
                capturedAt, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var captured))
            return "Updated: unknown";

        var elapsed = DateTime.UtcNow - captured.ToUniversalTime();
        if (elapsed < TimeSpan.Zero)
            elapsed = TimeSpan.Zero;

        if (elapsed.TotalMinutes < 3)
        {
            var minutes = (int)elapsed.TotalMinutes;
            var seconds = elapsed.Seconds;
            return minutes > 0 ? $"Updated {minutes}m {seconds}s ago" : $"Updated {seconds}s ago";
        }

        var wholeMinutes = (int)elapsed.TotalMinutes;
        return wholeMinutes == 1 ? "Updated 1 min ago" : $"Updated {wholeMinutes} min ago";
    }

    // Same priority as the website's filteredListings() sort: freshness
    // first (see FreshnessRank), then the duty's real release order
    // (newest first) when known, falling back to category then
    // minItemLevel for anything DutyReleaseOrder doesn't cover.
    private static int CompareListings(PfListingSearchResult a, PfListingSearchResult b)
    {
        var freshnessDiff = MatchFreshness.Rank(a.CapturedAt) - MatchFreshness.Rank(b.CapturedAt);
        if (freshnessDiff != 0)
            return freshnessDiff;

        var orderA = DutyReleaseOrder.For(a.DutyName);
        var orderB = DutyReleaseOrder.For(b.DutyName);
        if (orderA != null && orderB != null && orderA != orderB)
            return orderB.Value - orderA.Value;
        if (orderA != null && orderB == null)
            return -1;
        if (orderA == null && orderB != null)
            return 1;

        var bucketDiff = MatchCategorizer.CategoryOrderIndex(MatchCategorizer.CategoryBucket(a))
            - MatchCategorizer.CategoryOrderIndex(MatchCategorizer.CategoryBucket(b));
        return bucketDiff != 0 ? bucketDiff : a.MinItemLevel - b.MinItemLevel;
    }

    private readonly Configuration _config;
    private readonly AlertPoller _alertPoller;
    private readonly Dictionary<string, Dalamud.Interface.Textures.ISharedImmediateTexture> _iconCache = new();

    // A dedicated, smaller icon font atlas entry for the play/stop toggle —
    // rather than ImGui.SetWindowFontScale, which doesn't rebuild the font
    // at a different size, it just stretches the already-rasterized
    // default-size glyph texture (fine near 1.0x, visibly blurry the
    // further the user's own font size/global scale settings push that
    // base texture away from this target). Sized relative to
    // FontDefaultSizePx — which already accounts for the user's own font
    // size setting — via OnPreBuild (re-run whenever the atlas rebuilds,
    // e.g. on a font/scale settings change) so this stays proportionally
    // correct instead of a fixed pixel guess baked in once at startup.
    private readonly IFontHandle _smallIconFont;

    private string? _localDataCenter;
    private string _searchText = "";

    public MatchListView(Configuration config, AlertPoller alertPoller)
    {
        _config = config;
        _alertPoller = alertPoller;

        _smallIconFont = Plugin.PluginInterface.UiBuilder.FontAtlas.NewDelegateFontHandle(e => e.OnPreBuild(
            tk => tk.AddFontAwesomeIconFont(new()
            {
                SizePx = Plugin.PluginInterface.UiBuilder.FontDefaultSizePx * 0.85f,
            })));
    }

    // listHeight <= 0 fills whatever vertical space is left in the current
    // window (standalone MatchesWindow); a fixed height keeps it bounded
    // when embedded in the larger StatusWindow alongside its settings.
    // compact drops everything but the refresh button and the list itself
    // — for MatchesWindow, where the category filter lives in StatusWindow
    // instead (see Configuration.AlertCategory) so there's no reason to
    // repeat that UI in the popout.
    public void Draw(float listHeight = 320f, bool compact = false, bool minimal = false)
    {
        _localDataCenter = GetLocalDataCenter();

        // Breakdown is out of the full total (AlertPoller.Matches — every
        // job/ilvl/DC-matching listing, ignoring the category/freshness
        // display filters entirely), not just whatever's category-filtered.
        var freshnessCounts = new int[MatchFreshness.Colors.Length];
        foreach (var listing in _alertPoller.Matches)
            freshnessCounts[MatchFreshness.Rank(listing.CapturedAt)]++;

        var categoryFiltered = _alertPoller.Matches.AsEnumerable();
        // A non-empty search box overrides the category tab entirely rather
        // than narrowing within it — typing a search is "show me this
        // specific thing across everything", not "...within Blue Mage".
        if (string.IsNullOrEmpty(_searchText))
        {
            if (!string.IsNullOrEmpty(_config.AlertCategory))
                categoryFiltered = categoryFiltered.Where(l => MatchCategorizer.CategoryBucket(l) == _config.AlertCategory);
        }
        else
        {
            var searchRegex = TryBuildSearchRegex(_searchText);
            // An invalid/incomplete pattern (e.g. mid-typing "(") shouldn't
            // blank the whole list — just stop filtering until it's valid.
            if (searchRegex != null)
                categoryFiltered = categoryFiltered.Where(l => MatchesSearch(l, searchRegex));
        }

        // Cumulative, not exact: picking Yellow means "green or yellow"
        // (i.e. "at least this fresh"), Red means "anything" — matches how
        // the labels read ("<3min", "<10min", ">10min" as a staleness cap,
        // not three mutually-exclusive buckets).
        var filtered = _config.AlertFreshness >= 0
            ? categoryFiltered.Where(l => MatchFreshness.Rank(l.CapturedAt) <= _config.AlertFreshness)
            : categoryFiltered;
        var visible = filtered.OrderBy(l => l, Comparer<PfListingSearchResult>.Create(CompareListings)).ToList();

        // No search/filter controls, no freshness/refresh row — a compact
        // header line (current category + count) plus one line per listing.
        // Filters still apply (AlertCategory/AlertFreshness/search box, same
        // as normal mode) — this just doesn't expose UI to change them, same
        // reasoning as compact's own category-filter omission below.
        if (minimal)
        {
            DrawMinimal(visible);
            return;
        }

        // Row 1: search, category tab filter — both answer "what am I even
        // looking at".
        ImGui.SetNextItemWidth(160 * ImGuiHelpers.GlobalScale);
        var searchIsInvalid = !string.IsNullOrEmpty(_searchText) && TryBuildSearchRegex(_searchText) == null;
        using (ImRaii.PushColor(ImGuiCol.Border, new Vector4(1f, 0.4f, 0.4f, 1f), searchIsInvalid))
        // Default FramePadding makes this noticeably taller than the
        // SmallButtons on the row below — shrink it to match.
        using (ImRaii.PushStyle(ImGuiStyleVar.FramePadding, ImGuiHelpers.ScaledVector2(4, 2)))
        {
            ImGui.InputTextWithHint("##pfexplorer-search", "Search / Regex", ref _searchText, 200);
        }
        if (searchIsInvalid && ImGui.IsItemHovered())
            ImGui.SetTooltip("Invalid regex — showing unfiltered results.");

        ImGui.SameLine();
        DrawCategoryFilter();

        // Only relevant once the Blue Mage bucket is actually selected.
        if (_config.AlertCategory == "BlueMage")
        {
            ImGui.SameLine();
            bool openSpellbook;
            using (ImRaii.PushFont(Plugin.PluginInterface.UiBuilder.FontIcon))
                openSpellbook = ImGui.SmallButton($"{FontAwesomeIcon.Book.ToIconString()}##open-blue-spellbook");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Open Blue Magic Spellbook");
            if (openSpellbook)
                PfListingOpener.OpenBlueMageSpellbook();
        }

        // Row 2: results breakdown, then play/stop + refresh (+ its own
        // countdown) on the same line.
        ImGui.TextUnformatted($"{visible.Count} results out of");
        for (var rank = 0; rank < freshnessCounts.Length; rank++)
        {
            ImGui.SameLine();

            // The freshness filter is cumulative (picking Yellow means
            // "green or yellow", Red means "anything" — see Draw's own
            // filtering above), so every tier at or below the selected one
            // is actually included in the results right now, not just the
            // exact one clicked — highlight all of them, not only the
            // single selected rank.
            var isIncluded = _config.AlertFreshness >= 0 && rank <= _config.AlertFreshness;

            using (ImRaii.PushColor(ImGuiCol.Text, MatchFreshness.Colors[rank]))
                ImGui.TextUnformatted($"({freshnessCounts[rank]})");

            if (isIncluded)
            {
                var rankMin = ImGui.GetItemRectMin();
                var rankMax = ImGui.GetItemRectMax();
                var rankColor = MatchFreshness.Colors[rank];
                var fillColor = new Vector4(rankColor.X, rankColor.Y, rankColor.Z, 0.2f);
                var drawList = ImGui.GetWindowDrawList();
                // Faint fill so the text itself stays readable, plus an
                // underline right under the baseline — "highlighted", not
                // just outlined.
                drawList.AddRectFilled(rankMin, rankMax, ImGui.GetColorU32(fillColor), 2f * ImGuiHelpers.GlobalScale);
                drawList.AddLine(new Vector2(rankMin.X, rankMax.Y), new Vector2(rankMax.X, rankMax.Y), ImGui.GetColorU32(rankColor), 2f * ImGuiHelpers.GlobalScale);
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip($"Click to filter to {MatchFreshness.Labels[rank]}");
                if (ImGui.IsItemClicked())
                {
                    _config.AlertFreshness = rank;
                    _config.Save();
                    _alertPoller.RequestPoll();
                }
            }
        }

        ImGui.SameLine();
        var isRunning = _config.AlertEnabled;
        bool toggled;
        using (_smallIconFont.Push())
            toggled = ImGui.SmallButton($"{(isRunning ? FontAwesomeIcon.Stop : FontAwesomeIcon.Play).ToIconString()}##alert-toggle");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(isRunning ? "Stop polling" : "Start polling");
        if (toggled)
        {
            _config.AlertEnabled = !isRunning;
            _config.Save();
            if (_config.AlertEnabled)
            {
                _alertPoller.ResetBaseline();
                _alertPoller.RequestPoll();
            }
        }

        ImGui.SameLine();
        bool refreshClicked;
        using (ImRaii.Disabled(!_alertPoller.CanManualRefresh))
        {
            var refreshLabel = _alertPoller.NextPollAt is { } nextPoll
                ? $"Refresh ({FormatElapsed(nextPoll - DateTime.UtcNow)})"
                : "Refresh";
            refreshClicked = ImGui.SmallButton(refreshLabel);
        }
        if (refreshClicked)
            _alertPoller.RequestPoll();

        if (_alertPoller.LastError is { } alertError)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, new Vector4(1f, 0.4f, 0.4f, 1f)))
                ImGui.TextWrapped($"Last error: {alertError}");
        }

        if (visible.Count == 0)
        {
            ImGui.TextDisabled("No matching listings right now.");
            return;
        }

        using var matchesChild = ImRaii.Child("##alert-matches", new Vector2(0, listHeight), true);
        // A table instead of one big vertical text block per listing — icon,
        // duty (+ description/tags stacked within just that cell), world/DC/
        // recruiter, and the travel button all sit side by side per row,
        // same left-to-right reading order as the website's listing rows.
        // BordersInnerH alone renders too faint against the freshness row
        // tint to actually read as a separator — pushed to a more visible
        // (still subtle) gray below so rows are clearly delimited.
        using (ImRaii.PushColor(ImGuiCol.TableBorderLight, new Vector4(1f, 1f, 1f, 0.16f)))
        using (ImRaii.PushStyle(ImGuiStyleVar.CellPadding, ImGuiHelpers.ScaledVector2(6f, 4f)))
        using (var table = ImRaii.Table("##alert-matches-table", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH))
        {
            if (table)
            {
                // Wide enough for two 22x22 icons + gap (Blue Mage rows) as well
                // as the normal single 32x32 icon.
                ImGui.TableSetupColumn("##icon", ImGuiTableColumnFlags.WidthFixed, 50 * ImGuiHelpers.GlobalScale);
                ImGui.TableSetupColumn("##duty", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("##world", ImGuiTableColumnFlags.WidthFixed, 120 * ImGuiHelpers.GlobalScale);
                ImGui.TableSetupColumn("##travel", ImGuiTableColumnFlags.WidthFixed, 36 * ImGuiHelpers.GlobalScale);

                foreach (var listing in visible)
                    DrawMatchRow(listing);
            }
        }
    }

    private void DrawMatchRow(PfListingSearchResult listing)
    {
        var isNew = _alertPoller.NewMatchIds.Contains(listing.ListingId);
        var dutyName = string.IsNullOrEmpty(listing.DutyName) ? listing.Category : listing.DutyName;

        // Each column's content is wrapped in Begin/EndGroup so the whole
        // column — every line plus the gaps between them, not just the
        // exact pixels of a specific icon/text widget — acts as a single
        // hoverable/clickable region via one IsItemHovered/IsItemClicked
        // check right after EndGroup (a documented ImGui pattern for
        // treating a widget cluster as one item). A true single hit-rect
        // for the entire row, spanning column boundaries too, was tried
        // before and turned out fragile against this table's per-row
        // variable height; per-column groups get the same "click anywhere
        // you're reading" result without that risk.
        void HandleColumnClick()
        {
            if (!ImGui.IsItemHovered())
                return;

            ImGui.SetTooltip("Click to view in-game");
            if (ImGui.IsItemClicked())
                PfListingOpener.Open(listing);
        }

        ImGui.TableNextRow();
        ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(FreshnessBg(MatchFreshness.Rank(listing.CapturedAt))));

        ImGui.TableNextColumn();
        using (ImRaii.Group())
        {
            var isDualIcon = MatchCategorizer.CategoryBucket(listing) == "BlueMage" && listing.Category != "BlueMage";
            DrawCategoryIcons(listing, (isDualIcon ? 22 : 32) * ImGuiHelpers.GlobalScale, static () => { });
        }
        HandleColumnClick();

        ImGui.TableNextColumn();
        using (ImRaii.Group())
        {
            using (ImRaii.PushColor(ImGuiCol.Text, GoldColor, isNew))
                ImGui.TextUnformatted(dutyName);
            if (isNew)
            {
                ImGui.SameLine();
                ImGui.TextDisabled("(new)");
            }

            if (!_config.AlertHideDescription && !string.IsNullOrEmpty(listing.Description))
                ImGui.TextWrapped(listing.Description);

            DrawSlots(listing);

            var firstTag = true;
            foreach (var tag in listing.Tags)
            {
                if (!TagMeta.TryGetValue(tag, out var meta))
                    continue;

                if (!firstTag)
                    ImGui.SameLine();
                firstTag = false;

                using (ImRaii.PushColor(ImGuiCol.Text, meta.Color))
                    ImGui.TextUnformatted($"[{meta.Label}]");
            }
        }
        HandleColumnClick();

        ImGui.TableNextColumn();
        using (ImRaii.Group())
        {
            using (ImRaii.PushColor(ImGuiCol.Text, Vector4.One))
                ImGui.TextWrapped($"{listing.Name} ({listing.World})");
            ImGui.TextDisabled(FormatLastUpdated(listing.CapturedAt));
        }
        HandleColumnClick();

        ImGui.TableNextColumn();
        // No point offering to travel to the DC you're already standing in.
        var alreadyThere = _localDataCenter != null
            && string.Equals(_localDataCenter, listing.DataCenter, StringComparison.OrdinalIgnoreCase);
        if (!alreadyThere)
        {
            // The World Visit System only allows cross-DC travel within
            // your own region (NA/EU/JP) — except Oceania, which is exempt
            // from that restriction in both directions, so it's always
            // reachable regardless of where you're currently standing.
            var localRegion = DataCenterRegions.RegionOf(_localDataCenter);
            var targetRegion = DataCenterRegions.RegionOf(listing.DataCenter);
            var canTravel = (targetRegion == "OCE" || (localRegion != null && localRegion == targetRegion))
                && PfListingOpener.IsInOpenWorld;

            // Icon-only instead of a "Travel" text label — the column was
            // mostly empty space around that one word; the tooltip covers
            // what it does without needing to spell it out.
            bool clicked;
            using (ImRaii.Disabled(!canTravel))
            using (ImRaii.PushFont(Plugin.PluginInterface.UiBuilder.FontIcon))
                clicked = ImGui.SmallButton($"{FontAwesomeIcon.PlaneDeparture.ToIconString()}##travel-{listing.Id}");
            // BeginDisabled makes IsItemHovered() return false by default —
            // AllowWhenDisabled is needed to still get the tooltip telling
            // you *why* it's disabled, which is the whole point here.
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                string tooltip;
                if (canTravel)
                    tooltip = $"Travel to {listing.DataCenter}";
                else if (!PfListingOpener.IsInOpenWorld)
                    tooltip = "Can't travel right now — only available out in the open world";
                else if (targetRegion == null)
                    // A data center we don't recognize at all (not in
                    // DataCenterRegions.All) — no region to explain, so
                    // don't guess at one.
                    tooltip = "Cannot travel here";
                else
                    tooltip = $"Can't travel to {listing.DataCenter} — different region ({targetRegion}), and only Oceania allows cross-region travel";
                ImGui.SetTooltip(tooltip);
            }
            // Same confirmation popup PfListingOpener.Open uses for a
            // clicked result in a different DC — one travel flow, not two
            // that behave differently depending which button you clicked.
            if (clicked && canTravel)
                PfListingOpener.RequestTravel(listing.DataCenter, listing.World);
        }
    }

    // "30s", "5m", "1h20m" — same compact style as the website's freshness
    // display, just without rounding to whole minutes so "Last checked"
    // stays accurate right after a poll (which fires every 30s).
    private static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
            elapsed = TimeSpan.Zero;

        if (elapsed.TotalHours >= 1)
            return $"{(int)elapsed.TotalHours}h{elapsed.Minutes}m";
        if (elapsed.TotalMinutes >= 1)
            return $"{(int)elapsed.TotalMinutes}m";
        return $"{(int)elapsed.TotalSeconds}s";
    }

    // Cached so retyping/backspacing doesn't recompile the same pattern
    // every single frame while the list is redrawn.
    private string? _compiledSearchPattern;
    private Regex? _compiledSearchRegex;

    private Regex? TryBuildSearchRegex(string pattern)
    {
        if (pattern == _compiledSearchPattern)
            return _compiledSearchRegex;

        _compiledSearchPattern = pattern;
        try
        {
            _compiledSearchRegex = new Regex(pattern, RegexOptions.IgnoreCase);
        }
        catch (ArgumentException)
        {
            _compiledSearchRegex = null;
        }

        return _compiledSearchRegex;
    }

    private static bool MatchesSearch(PfListingSearchResult listing, Regex regex) =>
        regex.IsMatch(listing.DutyName ?? "")
        || regex.IsMatch(listing.Description ?? "")
        || regex.IsMatch(listing.Name ?? "")
        || regex.IsMatch(listing.World ?? "");

    // Same "Show only" filter as StatusWindow's DrawCategoryDropdown (both
    // just read/write Configuration.AlertCategory) — a compact copy here
    // too since this row already groups everything answering "what am I
    // looking at" (search, category, freshness-via-last-checked) in one
    // place instead of needing the Options window open just to change tabs.
    private void DrawCategoryFilter()
    {
        ImGui.SetNextItemWidth(160 * ImGuiHelpers.GlobalScale);

        // Counted against whatever the freshness filter currently allows
        // through (same cumulative "at least this fresh" rule Draw applies
        // to the visible list) — NOT the unfiltered full total. Counting
        // against the full total looked right but was actively misleading:
        // a bucket could show e.g. "(4)" from matches that are all yellow/
        // red, then show zero rows once selected because the freshness
        // filter (green-only by default) hides every one of them. "All"
        // is the freshness-filtered grand total, everything else is how
        // many of those fall in that bucket. A bucket with nothing in it
        // gets colored red instead of showing a "(0)" that's easy to miss.
        var freshnessAllowed = _config.AlertFreshness >= 0
            ? _alertPoller.Matches.Where(l => MatchFreshness.Rank(l.CapturedAt) <= _config.AlertFreshness)
            : _alertPoller.Matches.AsEnumerable();

        var counts = new Dictionary<string, int>();
        var totalCount = 0;
        foreach (var listing in freshnessAllowed)
        {
            var bucket = MatchCategorizer.CategoryBucket(listing);
            counts[bucket] = counts.GetValueOrDefault(bucket) + 1;
            totalCount++;
        }

        int CountFor(string value) => string.IsNullOrEmpty(value) ? totalCount : counts.GetValueOrDefault(value);

        // "All" (empty value) stays pinned first — it's the "clear the
        // filter" option, not a category to rank against the rest.
        // Everything else sorts by CountFor descending (ties broken by the
        // original CategoryOrder via a stable sort) so the categories with
        // matches right now are what you see first, instead of hunting
        // through a fixed alphabetical-ish order for whichever ones are
        // non-empty.
        var options = MatchCategorizer.CategoryOptions
            .OrderByDescending(o => string.IsNullOrEmpty(o.Value) ? int.MaxValue : CountFor(o.Value))
            .ToList();

        var currentIndex = options.FindIndex(o => o.Value == _config.AlertCategory);
        if (currentIndex < 0)
            currentIndex = 0;
        var (currentValue, currentLabel) = options[currentIndex];
        var currentCount = CountFor(currentValue);
        var currentIsZero = currentCount == 0;

        // HeightLargest: let the popup grow to fit every category instead
        // of ImGui's default ~8-item scroll window — there are only ~17
        // entries total, easily fits without needing to scroll to find one.
        // The text-color push is scoped tightly around just the combo's own
        // Begin call — it colors the closed preview, not the dropdown body
        // once it's open, so it can't stay pushed for the ImRaii.Combo's
        // whole using scope below.
        ImRaii.ComboDisposable combo;
        using (ImRaii.PushColor(ImGuiCol.Text, new Vector4(1f, 0.4f, 0.4f, 1f), currentIsZero))
            combo = ImRaii.Combo("##pfexplorer-category-filter", $"{currentLabel} ({currentCount})", ImGuiComboFlags.HeightLargest);

        using (combo)
        {
            if (combo)
            {
                foreach (var (value, label) in options)
                {
                    var count = CountFor(value);
                    var isZero = count == 0;
                    var isSelected = value == _config.AlertCategory;

                    bool selected;
                    using (ImRaii.PushColor(ImGuiCol.Text, new Vector4(1f, 0.4f, 0.4f, 1f), isZero))
                        selected = ImGui.Selectable($"{label} ({count})", isSelected);
                    if (selected)
                    {
                        _config.AlertCategory = value;
                        _config.Save();
                        _alertPoller.RequestPoll();
                    }

                    if (isSelected)
                        ImGui.SetItemDefaultFocus();
                }
            }
        }
    }

    public static string? GetLocalDataCenter()
    {
        try
        {
            var player = Plugin.ObjectTable.LocalPlayer;
            return player?.CurrentWorld.ValueNullable?.DataCenter.ValueNullable?.Name.ExtractText();
        }
        catch
        {
            // Lumina row lookups can throw if a RowRef points at an
            // unloaded/invalid row — never let that take the window down.
            return null;
        }
    }

    private void DrawSlots(PfListingSearchResult listing)
    {
        var firstItem = true;

        void NextItem()
        {
            if (!firstItem)
                ImGui.SameLine(0, 3 * ImGuiHelpers.GlobalScale);
            firstItem = false;
        }

        foreach (var job in listing.JobsPresent)
        {
            NextItem();
            var color = JobRoleColor.TryGetValue(job, out var c) ? c : DpsColor;
            DrawSlotSquare(color, 1f, job);
        }

        // Same trim as the website's getOpenSlotJobLists: cap to the real
        // remaining capacity in case the raw data has more entries than
        // that (old captures, padding, etc).
        var openCount = Math.Max(0, listing.SlotsAvailable - listing.SlotsFilled);
        var shownOpen = listing.OpenSlotJobs.Take(openCount).ToList();
        foreach (var accepted in shownOpen)
        {
            NextItem();

            Vector4 color;
            string tooltip;
            if (accepted.Count == 0)
            {
                color = AnyRoleColor;
                tooltip = "Any job (open)";
            }
            else
            {
                var roleColors = accepted
                    .Select(j => JobRoleColor.TryGetValue(j, out var c) ? c : DpsColor)
                    .Distinct()
                    .ToList();
                color = roleColors.Count == 1 ? roleColors[0] : AnyRoleColor;
                tooltip = $"{string.Join(", ", accepted)} (open)";
            }

            // Same idea as the website's .job-chip.open-slot { opacity: 0.6 }
            // — open seats get the role color dimmed instead of a different
            // color, so filled vs. open reads at a glance without losing
            // which role is needed. Dimmed further (0.5) so it reads even
            // less "filled" than the website's own version.
            DrawSlotSquare(color, 0.5f, tooltip);
        }

        var unaccounted = openCount - shownOpen.Count;
        if (unaccounted > 0)
        {
            NextItem();
            ImGui.TextDisabled($"+{unaccounted} open");
        }
    }

    private static void DrawSlotSquare(Vector4 color, float alpha, string tooltip)
    {
        var drawColor = new Vector4(color.X, color.Y, color.Z, color.W * alpha);
        var size = ImGuiHelpers.ScaledVector2(14, 14);
        var pos = ImGui.GetCursorScreenPos();
        ImGui.GetWindowDrawList().AddRectFilled(pos, pos + size, ImGui.GetColorU32(drawColor), 2f * ImGuiHelpers.GlobalScale);
        ImGui.Dummy(size);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(tooltip);
    }

    // Shared between the normal table row and the minimal-mode row — a Blue
    // Mage bucket listing reclassified from real duty content (see
    // MatchCategorizer.BlueMageEligibleCategories) shows both icons (real
    // duty type first, Blue Mage second) at `size`; genuine raw-BlueMage
    // content and everything else shows just its own single icon. onEachIcon
    // is called right after every image drawn so callers can hook
    // hover/click behavior consistently regardless of how many icons ended
    // up rendering.
    private void DrawCategoryIcons(PfListingSearchResult listing, float size, Action onEachIcon)
    {
        var bucket = MatchCategorizer.CategoryBucket(listing);
        if (bucket == "BlueMage" && listing.Category != "BlueMage")
        {
            var typeIcon = GetCategoryIcon(listing.Category);
            if (typeIcon != null)
            {
                ImGui.Image(typeIcon.Handle, new Vector2(size, size));
                onEachIcon();
                ImGui.SameLine(0, 2 * ImGuiHelpers.GlobalScale);
            }

            var blueMageIcon = GetCategoryIcon("BlueMage");
            if (blueMageIcon != null)
            {
                ImGui.Image(blueMageIcon.Handle, new Vector2(size, size));
                onEachIcon();
            }
        }
        else
        {
            var icon = GetCategoryIcon(bucket);
            if (icon != null)
            {
                ImGui.Image(icon.Handle, new Vector2(size, size));
                onEachIcon();
            }
        }
    }

    // Header line ("<category> (<count>)") plus one compact line per
    // listing — small icon(s), duty title, world/DC, click to open. No
    // search/category/freshness controls (see Draw's own comment).
    private void DrawMinimal(List<PfListingSearchResult> visible)
    {
        var categoryLabel = string.IsNullOrEmpty(_config.AlertCategory)
            ? "All"
            : MatchCategorizer.CategoryLabel(_config.AlertCategory);
        ImGui.TextUnformatted($"{categoryLabel} ({visible.Count})");

        // Same as the full view's DrawCategoryFilter — only relevant once
        // the Blue Mage bucket is actually selected.
        if (_config.AlertCategory == "BlueMage")
        {
            ImGui.SameLine();
            bool openSpellbook;
            using (ImRaii.PushFont(Plugin.PluginInterface.UiBuilder.FontIcon))
                openSpellbook = ImGui.SmallButton($"{FontAwesomeIcon.Book.ToIconString()}##open-blue-spellbook-minimal");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Open Blue Magic Spellbook");
            if (openSpellbook)
                PfListingOpener.OpenBlueMageSpellbook();
        }

        if (visible.Count == 0)
        {
            ImGui.TextDisabled("No matching listings right now.");
            return;
        }

        ImGui.Separator();

        foreach (var listing in visible)
            DrawMinimalRow(listing);
    }

    private void DrawMinimalRow(PfListingSearchResult listing)
    {
        var dutyName = string.IsNullOrEmpty(listing.DutyName) ? listing.Category : listing.DutyName;

        // Same green/yellow/red freshness tint as the normal table rows
        // (FreshnessBg) — painted manually since minimal mode isn't a table
        // here (no TableSetBgColor to lean on), sized to cover the 22px
        // icon plus a little breathing room.
        var rowHeight = Math.Max(22f * ImGuiHelpers.GlobalScale, ImGui.GetTextLineHeight()) + 4f * ImGuiHelpers.GlobalScale;
        var rowStart = ImGui.GetCursorScreenPos();
        var rowWidth = ImGui.GetContentRegionAvail().X;
        var rowEnd = new Vector2(rowStart.X + rowWidth, rowStart.Y + rowHeight);
        ImGui.GetWindowDrawList().AddRectFilled(rowStart, rowEnd, ImGui.GetColorU32(FreshnessBg(MatchFreshness.Rank(listing.CapturedAt))));

        // A real full-row hit target, not per-widget — this row (unlike the
        // full table's) is a single fixed-height line, so its bounds are
        // known upfront: draw an invisible Selectable spanning the whole
        // row, then reset the cursor back to rowStart so the actual icon/
        // text content draws on top of it in the same space. Nothing drawn
        // afterward (Image/Text) is itself interactive, so there's no
        // overlap conflict to resolve.
        // Same "do I need to travel, and can I" logic as the full table's
        // dedicated Travel button/column — used below for an icon rather
        // than a separate clickable button, since the whole row already
        // triggers PfListingOpener.Open (which itself prompts/errors on
        // travel as needed — see PfListingOpener.Open) regardless of where
        // on the row you click.
        var alreadyThere = _localDataCenter != null
            && string.Equals(_localDataCenter, listing.DataCenter, StringComparison.OrdinalIgnoreCase);
        var localRegion = DataCenterRegions.RegionOf(_localDataCenter);
        var targetRegion = DataCenterRegions.RegionOf(listing.DataCenter);
        var canTravel = (targetRegion == "OCE" || (localRegion != null && localRegion == targetRegion))
            && PfListingOpener.IsInOpenWorld;
        var needsTravel = !alreadyThere;

        ImGui.SetCursorScreenPos(rowStart);
        var rowClicked = ImGui.Selectable($"##row-{listing.Id}", false, ImGuiSelectableFlags.None, new Vector2(rowWidth, rowHeight));
        if (ImGui.IsItemHovered())
        {
            string tooltip;
            if (!needsTravel)
                tooltip = "Click to view in-game";
            else if (canTravel)
                tooltip = $"Click to travel to {listing.DataCenter} to view in-game";
            else if (!PfListingOpener.IsInOpenWorld)
                tooltip = "Can't travel right now — only available out in the open world";
            else
                tooltip = $"Can't travel to {listing.DataCenter} — different region ({targetRegion ?? "unknown"}), and only Oceania allows cross-region travel";
            ImGui.SetTooltip(tooltip);
        }
        ImGui.SetCursorScreenPos(rowStart);

        DrawCategoryIcons(listing, 22 * ImGuiHelpers.GlobalScale, static () => { });
        ImGui.SameLine(0, 4 * ImGuiHelpers.GlobalScale);
        ImGui.TextDisabled($"({listing.SlotsFilled}/{listing.SlotsAvailable})");
        ImGui.SameLine();
        ImGui.TextUnformatted(dutyName);
        ImGui.SameLine();
        ImGui.TextDisabled($"{listing.World} ({listing.DataCenter})");
        ImGui.SameLine();
        // Compact "1m"/"30m"/"1h20m" style (FormatElapsed, already used for
        // the refresh countdown) rather than normal mode's "Updated Xm ago"
        // — minimal mode is meant to be glanceable, not verbose. Unlike the
        // refresh countdown, seconds-level precision isn't useful here —
        // under a minute just reads "0m" instead of e.g. "45s".
        var elapsed = ElapsedSince(listing.CapturedAt);
        ImGui.TextDisabled(elapsed.TotalMinutes < 1 ? "0m" : FormatElapsed(elapsed));

        if (needsTravel)
        {
            // Right-aligned, plain icon (not a button) — a visual "you'll
            // need to travel for this one" flag, not a second click target;
            // the row itself already handles the click. Dimmed when travel
            // isn't even possible, same distinction the full table's
            // disabled Travel button makes.
            ImGui.SameLine(rowWidth - 18 * ImGuiHelpers.GlobalScale);
            using (ImRaii.PushColor(ImGuiCol.Text, canTravel ? Vector4.One : new Vector4(0.5f, 0.5f, 0.5f, 1f)))
            using (ImRaii.PushFont(Plugin.PluginInterface.UiBuilder.FontIcon))
                ImGui.TextUnformatted(FontAwesomeIcon.PlaneDeparture.ToIconString());
        }

        if (rowClicked)
            PfListingOpener.Open(listing);

        // A thin separator at the row's bottom edge — minimal mode has no
        // table to lean on BordersInnerH from, so this is drawn by hand,
        // same draw list/rect approach as the freshness tint above.
        ImGui.GetWindowDrawList().AddLine(
            new Vector2(rowStart.X, rowEnd.Y), new Vector2(rowEnd.X, rowEnd.Y),
            ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.12f)));

        // Content drawn after resetting the cursor back to rowStart left it
        // wherever the last SameLine chain ended, not at the row's actual
        // bottom — explicitly advance past the row (matching the Selectable's
        // own height) so the next row starts in the right place.
        ImGui.SetCursorScreenPos(new Vector2(rowStart.X, rowEnd.Y + 1 * ImGuiHelpers.GlobalScale));
    }

    private static TimeSpan ElapsedSince(string capturedAt)
    {
        if (!DateTime.TryParse(
                capturedAt, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var captured))
            return TimeSpan.Zero;

        var elapsed = DateTime.UtcNow - captured.ToUniversalTime();
        return elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
    }

    private IDalamudTextureWrap? GetCategoryIcon(string category)
    {
        if (!CategoryIconFiles.TryGetValue(category, out var fileName))
            return null;

        // Cache the *handle* (cheap, stable), not the wrap it currently
        // resolves to — GetFromFile's texture loads asynchronously, so the
        // first several frames legitimately return null from
        // GetWrapOrDefault() before the file's finished loading. Caching
        // that null would've locked the icon "off" forever; calling
        // GetWrapOrDefault() fresh every draw (a cheap lookup into
        // Dalamud's own texture cache, not a re-read of the file) picks it
        // up as soon as it's ready.
        if (!_iconCache.TryGetValue(fileName, out var shared))
        {
            var iconsDir = Path.Combine(Plugin.PluginInterface.AssemblyLocation.DirectoryName ?? ".", "icons");
            var path = Path.Combine(iconsDir, fileName);
            shared = Plugin.TextureProvider.GetFromFile(path);
            _iconCache[fileName] = shared;
        }

        return shared.GetWrapOrDefault();
    }

    public void Dispose()
    {
        _smallIconFont.Dispose();
        // Textures came from ITextureProvider.GetFromFile, which is a
        // shared/cached lookup by path — Dalamud owns that lifecycle, this
        // just drops our own references to it.
        _iconCache.Clear();
    }
}
