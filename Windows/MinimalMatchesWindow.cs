using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;

namespace PfExplorer.Windows;

// The compact alternative to MatchesWindow — a genuinely separate Window
// (own stable ##id) rather than a content branch inside MatchesWindow, so
// Dalamud's per-window position/size persistence keeps its own layout
// independent of the full view's. Only one of the two is ever open at a
// time (see Plugin's SwitchToMinimal/SwitchToFull wiring) — this isn't a
// second, simultaneously-usable window, just the other shape the one
// conceptual "results window" can take.
public class MinimalMatchesWindow : Window, IDisposable
{
    private readonly MatchListView _matchListView;

    public Action? OnOpenOptions { get; set; }

    // Closes this window and opens MatchesWindow.
    public Action? SwitchToFull { get; set; }

    public MinimalMatchesWindow(MatchListView matchListView)
        : base("PF Explorer Mini##pfexplorer-matches-mini")
    {
        _matchListView = matchListView;

        TitleBarButtons.Add(new TitleBarButton
        {
            Icon = FontAwesomeIcon.Cog,
            IconOffset = new Vector2(2, 1),
            ShowTooltip = () => ImGui.SetTooltip("Options"),
            Click = _ => OnOpenOptions?.Invoke(),
        });

        TitleBarButtons.Add(new TitleBarButton
        {
            Icon = FontAwesomeIcon.Expand,
            IconOffset = new Vector2(2, 1),
            ShowTooltip = () => ImGui.SetTooltip("Show full view"),
            Click = _ => SwitchToFull?.Invoke(),
        });

        // Smaller default than MatchesWindow's 480x420 — the whole point of
        // this window is being small/glanceable. FirstUseEver so it's just
        // the starting size; a real resize is remembered after that.
        Size = new Vector2(260, 220);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    // Same StatusWindow-gluing purpose as MatchesWindow's own
    // LastPosition/LastSize — see that file's comment for why this can't
    // just read Window.Position/Size instead.
    public Vector2 LastPosition { get; private set; }
    public Vector2 LastSize { get; private set; }

    public override void Draw()
    {
        LastPosition = ImGui.GetWindowPos();
        LastSize = ImGui.GetWindowSize();

        _matchListView.Draw(0, compact: true, minimal: true);
    }

    public void Dispose()
    {
    }
}
