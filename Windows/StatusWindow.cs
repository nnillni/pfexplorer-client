using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace PfExplorer.Windows;

// "Options" window — opened via the cog button on MatchesWindow (the
// primary results window) or Dalamud's own "Configure" entry. Two tabs:
// "Party Finder" (the alert filters/notifications) and "Upload" (the
// capture/contribute-to-the-server toggle). No results here — see
// MatchListView/MatchesWindow for that.
public class StatusWindow : Window, IDisposable
{
    // Every "real" job worth alerting on — base classes excluded, same
    // grouping as the website's ROLE_JOB_SETS. BLU is deliberately not in
    // here: it's locked out of virtually all normal duty content, so it
    // gets its own row instead of sitting alongside Tank/Healer/DPS.
    private static readonly (string Label, string[] Jobs)[] JobGroups =
    {
        ("Tank", new[] { "PLD", "WAR", "DRK", "GNB" }),
        ("Healer", new[] { "WHM", "SCH", "AST", "SGE" }),
        ("DPS", new[]
        {
            "MNK", "DRG", "NIN", "SAM", "RPR", "VPR", "BRD", "MCH", "DNC", "BLM", "SMN", "RDM", "PCT",
        }),
    };


    private readonly Configuration _config;
    private readonly ListingUploader _uploader;
    private readonly AlertPoller _alertPoller;
    private readonly MatchesWindow _matchesWindow;
    private readonly MinimalMatchesWindow _minimalMatchesWindow;

    private string _serverUrlBuffer;

    // ImGui's tab bar remembers whichever tab was last active across
    // frames on its own — set by Plugin's own per-frame Draw hook (which
    // runs unconditionally every frame, unlike this window's own Draw,
    // PreDraw etc., which only fire while IsOpen) whenever it detects
    // IsOpen just flipped false->true, so Draw below can force-select
    // "Party Finder" the moment this window opens instead of reopening
    // onto whatever tab happened to be selected when it was last closed.
    public bool ForceFirstTabOnNextDraw { get; set; }

    public StatusWindow(
        Configuration config, ListingUploader uploader, AlertPoller alertPoller,
        MatchesWindow matchesWindow, MinimalMatchesWindow minimalMatchesWindow)
        : base("PF Explorer Options##pfexplorer-status")
    {
        _config = config;
        _uploader = uploader;
        _alertPoller = alertPoller;
        _matchesWindow = matchesWindow;
        _minimalMatchesWindow = minimalMatchesWindow;
        _serverUrlBuffer = config.ServerUrl;
        IsOpen = false;

        // Window.Size is already scaled by Dalamud's own GlobalScale
        // internally — a plain literal here, not ImGuiHelpers.ScaledVector2
        // (which would double-scale it).
        Size = new Vector2(420, 500);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    // Glues this window to the right edge of whichever results window is
    // currently open (MatchesWindow or MinimalMatchesWindow — only one ever
    // is, see Plugin's SwitchToMinimal/SwitchToFull wiring), using its
    // LastPosition/LastSize (the actual rendered rect, wherever it's been
    // dragged to — not just wherever we last told it to go). Calls
    // ImGui.SetNextWindowPos directly here rather than going through the
    // Position/PositionCondition properties (which didn't visibly take
    // effect — untested why, possibly some internal caching in the base
    // Window class) so this window can't be dragged independently while a
    // results window is open; it just follows along instead.
    public override void PreDraw()
    {
        Vector2 anchorPosition, anchorSize;
        if (_matchesWindow.IsOpen)
        {
            anchorPosition = _matchesWindow.LastPosition;
            anchorSize = _matchesWindow.LastSize;
        }
        else if (_minimalMatchesWindow.IsOpen)
        {
            anchorPosition = _minimalMatchesWindow.LastPosition;
            anchorSize = _minimalMatchesWindow.LastSize;
        }
        else
        {
            return;
        }

        var pos = anchorPosition + new Vector2(anchorSize.X, 0);
        ImGui.SetNextWindowPos(pos, ImGuiCond.Always);
    }

    public override void Draw()
    {
        var partyFinderTabFlags = ForceFirstTabOnNextDraw ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
        ForceFirstTabOnNextDraw = false;

        using var tabBar = ImRaii.TabBar("##pfexplorer-tabs");
        if (!tabBar)
            return;

        using (var partyFinderTab = ImRaii.TabItem("Party Finder", partyFinderTabFlags))
        {
            if (partyFinderTab)
                DrawPartyFinderTab();
        }

        using (var uploadTab = ImRaii.TabItem("Upload"))
        {
            if (uploadTab)
                DrawUploadTab();
        }

        using (var debugTab = ImRaii.TabItem("Debug"))
        {
            if (debugTab)
                DrawDebugTab();
        }
    }

    // Polls the server's search endpoint and lists/announces matches —
    // independent of the Upload tab's capture/contribute toggle, since you
    // might want alerts without contributing capture data, or vice versa.
    private void DrawPartyFinderTab()
    {
        // Starting/stopping polling itself now lives on the play/stop
        // button next to Refresh in MatchListView — these filters always
        // show here regardless of whether it's currently running, so you
        // can set everything up before pressing play.
        DrawJobsHeader();
        DrawItemLevelHeader();
        DrawDataCentersHeader();

        // Config field is still "exclude" internally (matches the server's
        // own excludeXivpf query param — see AlertPoller.PollAsync), just
        // inverted for display so the checkbox reads as an opt-in.
        var addXivpf = !_config.AlertExcludeXivpf;
        if (ImGui.Checkbox("Add xivpf.com results", ref addXivpf))
        {
            _config.AlertExcludeXivpf = !addXivpf;
            _config.Save();
            _alertPoller.ResetBaseline();
            _alertPoller.RequestPoll();
        }

        DrawFreshnessDropdown();
        DrawNotificationsHeader();

        ImGui.Spacing();
        var hideDescription = _config.AlertHideDescription;
        if (ImGui.Checkbox("Hide descriptions in list", ref hideDescription))
        {
            _config.AlertHideDescription = hideDescription;
            _config.Save();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.Button("Reset to defaults"))
            ResetToDefaults();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Clears jobs/item level/data centers/category/freshness filters and notification settings back to their defaults. Doesn't touch the Upload tab or whether polling is currently running.");
    }

    // Blank Configuration's own field defaults are the single source of
    // truth here — copying from a fresh instance instead of hardcoding the
    // values a second time keeps this from drifting out of sync with
    // Configuration.cs.
    private void ResetToDefaults()
    {
        var defaults = new Configuration();

        _config.AlertJobs = defaults.AlertJobs;
        _config.AlertIlvlMin = defaults.AlertIlvlMin;
        _config.AlertIlvlMax = defaults.AlertIlvlMax;

        // Same auto-detect as the first-run default (Plugin.
        // TryInitializeDefaults) rather than leaving this on defaults.
        // AlertDataCenters (empty/Any) — resetting should put you back
        // where a fresh install would've left you, not blank.
        var localDataCenter = MatchListView.GetLocalDataCenter();
        _config.AlertDataCenters = localDataCenter != null
            ? new List<string> { localDataCenter }
            : defaults.AlertDataCenters;

        _config.AlertExcludeXivpf = defaults.AlertExcludeXivpf;
        _config.AlertCategory = defaults.AlertCategory;
        _config.AlertFreshness = defaults.AlertFreshness;
        _config.AlertNotifyOnNewMatch = defaults.AlertNotifyOnNewMatch;
        _config.AlertNotifyOnPartyChange = defaults.AlertNotifyOnPartyChange;
        _config.AlertNotifyOnRemoved = defaults.AlertNotifyOnRemoved;
        _config.AlertNotifyChat = defaults.AlertNotifyChat;
        _config.AlertNotifyToast = defaults.AlertNotifyToast;
        _config.AlertNotifySound = defaults.AlertNotifySound;
        _config.AlertNewMatchColor = defaults.AlertNewMatchColor;
        _config.AlertPartyChangeColor = defaults.AlertPartyChangeColor;
        _config.AlertRemovedColor = defaults.AlertRemovedColor;
        _config.AlertHideDescription = defaults.AlertHideDescription;

        _config.Save();
        _alertPoller.ResetBaseline();
        _alertPoller.RequestPoll();
    }

    private void DrawUploadTab()
    {
        var enabled = _config.Enabled;
        if (ImGui.Checkbox("Capture & upload Party Finder listings", ref enabled))
        {
            _config.Enabled = enabled;
            _config.Save();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextUnformatted("Server URL");
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText("##server-url", ref _serverUrlBuffer, 256))
        {
            _config.ServerUrl = _serverUrlBuffer;
            _config.Save();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextUnformatted($"Captured this session: {_uploader.TotalCaptured}");
        ImGui.TextUnformatted($"Uploaded this session: {_uploader.TotalUploaded}");
        ImGui.TextUnformatted(_uploader.LastUploadAt is { } last
            ? $"Last upload: {last.ToLocalTime():HH:mm:ss}"
            : "Last upload: never");
        ImGui.TextUnformatted(_uploader.ContributorStats is { } stats
            ? $"Contributors: {stats.Active} active (last 15min) / {stats.Total} total"
            : "Contributors: —");

        if (_uploader.LastError is { } error)
        {
            ImGui.Spacing();
            using (ImRaii.PushColor(ImGuiCol.Text, new Vector4(1f, 0.4f, 0.4f, 1f)))
                ImGui.TextWrapped($"Last error: {error}");
        }
    }

    // Answers "is RequestCategoryListings actually giving us everything, or
    // just one page?" — reads the same native fields/nodes PfListingOpener
    // touches, straight off AgentLookingForGroup and the native
    // LookingForGroup addon (if it happens to be open), so you can see what
    // the last "All" request actually populated without guessing.
    private unsafe void DrawDebugTab()
    {
        ImGui.TextWrapped("Diagnostic view of the native PF agent/addon state — used to check how many results a RequestCategoryListings(\"All\") call actually returns per page.");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        DrawColorSwatchButton();

        // Fires all three Announce* -> SendNotification -> BuildColoredMessage
        // paths a real match/change/removal does, just with synthetic
        // listing data — so the colors (and the white duty-name/slot-count
        // spans) shown in chat are exactly what a real notification
        // renders, without waiting for one to happen.
        if (ImGui.Button("Test all 3 announcement types"))
            _alertPoller.TestAllAnnouncements();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var agent = AgentLookingForGroup.Instance();
        if (agent == null)
        {
            ImGui.TextDisabled("AgentLookingForGroup not available.");
            return;
        }

        ImGui.TextUnformatted($"NumberOfListingsDisplayed: {agent->NumberOfListingsDisplayed}");
        ImGui.TextUnformatted($"Listings.ListingIds count: {agent->Listings.ListingIds.Length}");
        ImGui.TextUnformatted($"CategoryTab: {agent->CategoryTab}");
        ImGui.TextUnformatted($"SearchAreaTab: {agent->SearchAreaTab}");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Only populated while the native PF window itself is actually
        // loaded — RequestCategoryListings updates the agent's data
        // regardless, but these text nodes are addon UI state, not agent
        // state, so they only reflect reality if the addon exists.
        var addonPtr = Plugin.GameGui.GetAddonByName("LookingForGroup", 1).Address;
        if (addonPtr == IntPtr.Zero)
        {
            ImGui.TextDisabled("LookingForGroup addon isn't loaded (open Party Finder in-game to see page/result text).");
            return;
        }

        var addon = (AddonLookingForGroup*)addonPtr;
        var resultsText = addon->ResultsCountTextNode != null ? addon->ResultsCountTextNode->NodeText.ToString() : "(none)";
        var pageText = addon->CurrentPageTextNode != null ? addon->CurrentPageTextNode->NodeText.ToString() : "(none)";
        ImGui.TextUnformatted($"ResultsCountTextNode: {resultsText}");
        ImGui.TextUnformatted($"CurrentPageTextNode: {pageText}");
    }

    // Prints one chat line per UIColor sheet row, each colored via that
    // row's own ID (Dalamud.Game.Text.SeStringHandling.SeStringBuilder.
    // AddUiForeground(ushort) — the same call AlertPoller.SendNotification
    // uses for the "(new)"/"(gone)" tags) — the row ID is the only thing
    // callers actually pass, the client resolves the real color from it at
    // render time, so this is the only reliable way to see what a given
    // row looks like in real chat rendering rather than guessing from a
    // decoded RGB value that might not match (see AlertPoller's own
    // RemovedColor history — 19 vs 518 looked fine on paper too until
    // someone actually looked at it in game).
    private void DrawColorSwatchButton()
    {
        if (ImGui.Button("Print all UIColor rows to chat"))
            PrintColorSwatches();
        ImGui.SameLine();
        ImGui.TextDisabled("(spams chat — one line per row, look for the row ID next to the color you want)");
    }

    private void PrintColorSwatches()
    {
        foreach (var (id, _) in GetUiColorSwatches())
        {
            var builder = new SeStringBuilder()
                .AddUiForeground((ushort)id)
                .AddText($"Row {id}: The quick brown fox")
                .AddUiForegroundOff();
            Plugin.ChatGui.Print(builder.Build());
        }
    }

    // Built once and cached for the process lifetime — the sheet doesn't
    // change at runtime, and this backs a combo box that gets rebuilt every
    // frame it's open. Color is decoded from the "Dark" column specifically:
    // UIColor has separate columns per UI color theme (Dark/Light/Classic/
    // Clear variants) added alongside the newer theme options, and AddUiForeground
    // resolves whichever one the player's own client theme is set to at
    // render time — Dark is FFXIV's default, so it's the closest single
    // preview to what most people (including whoever's picking a color
    // here) will actually see in their own chat log.
    private static List<(uint Id, Vector4 Color)>? _uiColorSwatches;

    private static List<(uint Id, Vector4 Color)> GetUiColorSwatches()
    {
        if (_uiColorSwatches != null)
            return _uiColorSwatches;

        var list = new List<(uint, Vector4)>();
        var sheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.UIColor>();
        if (sheet != null)
        {
            foreach (var row in sheet)
            {
                if (row.RowId == 0 || row.RowId > ushort.MaxValue)
                    continue;

                var rgba = row.Dark;
                var r = ((rgba >> 24) & 0xFF) / 255f;
                var g = ((rgba >> 16) & 0xFF) / 255f;
                var b = ((rgba >> 8) & 0xFF) / 255f;
                list.Add((row.RowId, new Vector4(r, g, b, 1f)));
            }
        }

        _uiColorSwatches = list;
        return list;
    }

    // Falls back to white if the configured row id isn't in the sheet for
    // some reason (shouldn't happen — pickers only ever write ids that came
    // from this same list — but a missing color shouldn't crash a Draw call).
    private static Vector4 GetSwatchColor(ushort id)
    {
        foreach (var (rowId, color) in GetUiColorSwatches())
        {
            if (rowId == id)
                return color;
        }
        return Vector4.One;
    }

    // A combo box where every option is rendered as its own actual color
    // (a swatch button next to the row ID), not just a numbered list — the
    // whole point being you can see what you're picking instead of guessing
    // from a row number, same as the Debug tab's "print all to chat" button
    // but without spamming chat every time you open a dropdown.
    private void DrawColorPicker(string id, Func<ushort> get, Action<ushort> set)
    {
        var current = get();
        var currentColor = GetSwatchColor(current);

        using var outerId = ImRaii.PushId(id);
        ImGui.ColorButton("##current", currentColor, ImGuiColorEditFlags.NoTooltip | ImGuiColorEditFlags.NoInputs, ImGuiHelpers.ScaledVector2(20, 20));
        ImGui.SameLine();

        using var combo = ImRaii.Combo("Color", $"Row {current}");
        if (combo)
        {
            foreach (var (rowId, color) in GetUiColorSwatches())
            {
                using var rowScopedId = ImRaii.PushId((int)rowId);
                ImGui.ColorButton("##swatch", color, ImGuiColorEditFlags.NoTooltip | ImGuiColorEditFlags.NoInputs, ImGuiHelpers.ScaledVector2(16, 16));
                ImGui.SameLine();
                if (ImGui.Selectable($"Row {rowId}", rowId == current))
                {
                    set((ushort)rowId);
                    _config.Save();
                }
            }
        }
    }

    // Collapsed by default — the header itself always shows a summary of
    // what's currently selected, so you don't need to expand it just to
    // remember what your own filter is set to.
    private void DrawJobsHeader()
    {
        var summary = _config.AlertJobs.Count == 0 ? "Any" : string.Join(", ", _config.AlertJobs);
        if (!ImGui.CollapsingHeader($"Jobs: {summary}###jobs-header"))
            return;

        foreach (var (label, jobs) in JobGroups)
            DrawJobRow(label, jobs);

        // BLU gets its own row rather than sitting in the DPS list — it's
        // shut out of an "any job" open seat (see AlertPoller.Matches_),
        // so checking it only matches a listing explicitly asking for BLU.
        DrawJobRow("Limited", new[] { "BLU" });
    }

    private void DrawItemLevelHeader()
    {
        var summary = _config.AlertIlvlMin == 0 && _config.AlertIlvlMax == 0
            ? "Any"
            : $"{(_config.AlertIlvlMin == 0 ? "0" : _config.AlertIlvlMin.ToString())}–{(_config.AlertIlvlMax == 0 ? "∞" : _config.AlertIlvlMax.ToString())}";
        if (!ImGui.CollapsingHeader($"Item level: {summary}###ilvl-header"))
            return;

        var ilvlMin = _config.AlertIlvlMin;
        ImGui.SetNextItemWidth(90 * ImGuiHelpers.GlobalScale);
        if (ImGui.InputInt("Min##ilvl-min", ref ilvlMin))
        {
            _config.AlertIlvlMin = Math.Max(0, ilvlMin);
            _config.Save();
            _alertPoller.ResetBaseline();
            _alertPoller.RequestPoll();
        }

        ImGui.SameLine();
        var ilvlMax = _config.AlertIlvlMax;
        ImGui.SetNextItemWidth(90 * ImGuiHelpers.GlobalScale);
        if (ImGui.InputInt("Max##ilvl-max", ref ilvlMax))
        {
            _config.AlertIlvlMax = Math.Max(0, ilvlMax);
            _config.Save();
            _alertPoller.ResetBaseline();
            _alertPoller.RequestPoll();
        }
    }

    private void DrawDataCentersHeader()
    {
        var summary = _config.AlertDataCenters.Count == 0 ? "Any" : string.Join(", ", _config.AlertDataCenters);
        if (!ImGui.CollapsingHeader($"Data centers: {summary}###dc-header"))
            return;

        foreach (var (region, dataCenters) in DataCenterRegions.All)
            DrawDataCenterRow(region, dataCenters);
    }

    // Same green/yellow/red buckets as the row tint (MatchFreshness.Rank) —
    // lets you filter down to e.g. only green (freshest/most accurate)
    // matches. Applies to notifications too (see AlertPoller.PollAsync), not
    // just what's displayed.
    private void DrawFreshnessDropdown()
    {
        ImGui.TextUnformatted("Freshness");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);

        var currentLabel = _config.AlertFreshness < 0 ? "Any" : MatchFreshness.Labels[_config.AlertFreshness];

        // The dropdown's own selectable rows were already colored per-status
        // below — this colors the closed combo's preview (the currently
        // selected value shown before you even open it) the same way, so
        // e.g. "Green" shows green whether the dropdown is open or closed,
        // not just while picking. "Any" has no single status to match, so
        // it stays the default text color.
        // Scoped tightly around just the combo's own Begin call — it colors
        // the closed preview, not the dropdown body once it's open, so it
        // can't stay pushed for the ImRaii.Combo's whole using scope below.
        Vector4? previewColor = _config.AlertFreshness >= 0 ? MatchFreshness.Colors[_config.AlertFreshness] : null;
        ImRaii.ComboDisposable combo;
        using (ImRaii.PushColor(ImGuiCol.Text, previewColor))
            combo = ImRaii.Combo("##freshness-filter", currentLabel);

        using (combo)
        {
            if (combo)
            {
                var isAnySelected = _config.AlertFreshness < 0;
                if (ImGui.Selectable("Any", isAnySelected))
                {
                    _config.AlertFreshness = -1;
                    _config.Save();
                    _alertPoller.RequestPoll();
                }

                if (isAnySelected)
                    ImGui.SetItemDefaultFocus();

                for (var rank = 0; rank < MatchFreshness.Labels.Length; rank++)
                {
                    var isSelected = _config.AlertFreshness == rank;
                    bool selected;
                    using (ImRaii.PushColor(ImGuiCol.Text, MatchFreshness.Colors[rank]))
                        selected = ImGui.Selectable(MatchFreshness.Labels[rank], isSelected);
                    if (selected)
                    {
                        _config.AlertFreshness = rank;
                        _config.Save();
                        _alertPoller.RequestPoll();
                    }

                    if (isSelected)
                        ImGui.SetItemDefaultFocus();
                }
            }
        }
    }

    // Any combination of the three, independently — e.g. chat + sound but
    // no toast is a perfectly reasonable setup. All off = silent (you'd
    // just notice a new row in the list on your own).
    private void DrawNotificationsHeader()
    {
        var triggers = new List<string>();
        if (_config.AlertNotifyOnNewMatch) triggers.Add("New");
        if (_config.AlertNotifyOnPartyChange) triggers.Add("Party change");
        if (_config.AlertNotifyOnRemoved) triggers.Add("Removed");
        var methods = new List<string>();
        if (_config.AlertNotifyChat) methods.Add("Chat");
        if (_config.AlertNotifyToast) methods.Add("Popup");
        if (_config.AlertNotifySound) methods.Add("Sound");
        var summary = triggers.Count == 0 || methods.Count == 0
            ? "Off"
            : $"{string.Join(" + ", triggers)} via {string.Join(" + ", methods)}";

        if (!ImGui.CollapsingHeader($"Notify: {summary}###notify-header"))
            return;

        ImGui.TextUnformatted("Notify on");

        // Each checkbox's own label is tinted with that event's configured
        // chat color (same lookup DrawColorPicker's preview swatch uses) —
        // so e.g. "New result found" actually reads green when that's what
        // AlertNewMatchColor is set to, matching what you'll actually see
        // in chat, not just a swatch floating unrelated next to plain text.
        var onNewMatch = _config.AlertNotifyOnNewMatch;
        bool newMatchChanged;
        using (ImRaii.PushColor(ImGuiCol.Text, GetSwatchColor(_config.AlertNewMatchColor)))
            newMatchChanged = ImGui.Checkbox("New result found", ref onNewMatch);
        if (newMatchChanged)
        {
            _config.AlertNotifyOnNewMatch = onNewMatch;
            _config.Save();
        }
        ImGui.SameLine();
        DrawColorPicker("##color-new", () => _config.AlertNewMatchColor, v => _config.AlertNewMatchColor = v);

        var onPartyChange = _config.AlertNotifyOnPartyChange;
        bool partyChangeChanged;
        using (ImRaii.PushColor(ImGuiCol.Text, GetSwatchColor(_config.AlertPartyChangeColor)))
            partyChangeChanged = ImGui.Checkbox("Party size changed", ref onPartyChange);
        if (partyChangeChanged)
        {
            _config.AlertNotifyOnPartyChange = onPartyChange;
            _config.Save();
        }
        ImGui.SameLine();
        DrawColorPicker("##color-changed", () => _config.AlertPartyChangeColor, v => _config.AlertPartyChangeColor = v);

        var onRemoved = _config.AlertNotifyOnRemoved;
        bool removedChanged;
        using (ImRaii.PushColor(ImGuiCol.Text, GetSwatchColor(_config.AlertRemovedColor)))
            removedChanged = ImGui.Checkbox("Result no longer available", ref onRemoved);
        if (removedChanged)
        {
            _config.AlertNotifyOnRemoved = onRemoved;
            _config.Save();
        }
        ImGui.SameLine();
        DrawColorPicker("##color-removed", () => _config.AlertRemovedColor, v => _config.AlertRemovedColor = v);

        ImGui.Spacing();
        ImGui.TextUnformatted("Deliver via");
        var chat = _config.AlertNotifyChat;
        if (ImGui.Checkbox("Echo in chat", ref chat))
        {
            _config.AlertNotifyChat = chat;
            _config.Save();
        }

        var toast = _config.AlertNotifyToast;
        if (ImGui.Checkbox("Popup on screen", ref toast))
        {
            _config.AlertNotifyToast = toast;
            _config.Save();
        }

        var sound = _config.AlertNotifySound;
        if (ImGui.Checkbox("Play a sound", ref sound))
        {
            _config.AlertNotifySound = sound;
            _config.Save();
        }
    }

    private void DrawJobRow(string label, string[] jobs)
    {
        ImGui.TextDisabled(label);
        ImGui.SameLine(80 * ImGuiHelpers.GlobalScale);
        for (var i = 0; i < jobs.Length; i++)
        {
            var job = jobs[i];
            var isChecked = _config.AlertJobs.Contains(job);
            if (ImGui.Checkbox(job, ref isChecked))
            {
                if (isChecked)
                    _config.AlertJobs.Add(job);
                else
                    _config.AlertJobs.Remove(job);
                _config.Save();
                _alertPoller.ResetBaseline();
                _alertPoller.RequestPoll();
            }

            if (i < jobs.Length - 1)
                ImGui.SameLine();
        }
    }

    private void DrawDataCenterRow(string region, string[] dataCenters)
    {
        ImGui.TextDisabled(region);
        ImGui.SameLine(80 * ImGuiHelpers.GlobalScale);
        for (var i = 0; i < dataCenters.Length; i++)
        {
            var dc = dataCenters[i];
            var isChecked = _config.AlertDataCenters.Contains(dc);
            if (ImGui.Checkbox(dc, ref isChecked))
            {
                if (isChecked)
                    _config.AlertDataCenters.Add(dc);
                else
                    _config.AlertDataCenters.Remove(dc);
                _config.Save();
                _alertPoller.ResetBaseline();
                _alertPoller.RequestPoll();
            }

            if (i < dataCenters.Length - 1)
                ImGui.SameLine();
        }
    }

    public void Dispose()
    {
    }
}
