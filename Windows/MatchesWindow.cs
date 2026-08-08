using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;

namespace PfExplorer.Windows;

// The plugin's primary window (opened by /pfexplorer and Dalamud's "open
// main UI") — just the live match list, no settings/config clutter, meant
// to sit visible on screen while actually playing. A cog button in the
// titlebar opens StatusWindow ("Options") for everything else. Shares the
// same MatchListView instance as StatusWindow, so the selected category
// tab, icon cache, and local-DC lookup aren't duplicated between the two.
public class MatchesWindow : Window, IDisposable
{
    private readonly MatchListView _matchListView;

    // Set by Plugin after both windows exist — can't take a StatusWindow
    // reference at construction time since StatusWindow's own constructor
    // needs this window too (for its "close" fallback check).
    public Action? OnOpenOptions { get; set; }

    // Closes this window and opens MinimalMatchesWindow — a genuinely
    // separate Window (its own stable ##id), not a content branch inside
    // this one. That used to be a single window toggling a `minimal` flag,
    // but Dalamud's window position/size persistence is keyed per-window,
    // so both "modes" were forced to share whatever size/position the user
    // last left the one shared window at. Two real windows each keep their
    // own independently.
    public Action? SwitchToMinimal { get; set; }

    public MatchesWindow(MatchListView matchListView)
        : base("PF Explorer##pfexplorer-matches")
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
            Icon = FontAwesomeIcon.Compress,
            IconOffset = new Vector2(2, 1),
            ShowTooltip = () => ImGui.SetTooltip("Minimal view"),
            Click = _ => SwitchToMinimal?.Invoke(),
        });

        Size = new Vector2(480, 420);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    // Captured every frame (right at the top of Draw(), which is
    // guaranteed to run between this window's Begin/End) so StatusWindow
    // can glue itself to this window's right edge regardless of where it's
    // been dragged to — Window.Position/Size only reflect what WE
    // explicitly assigned, not wherever the user's actually moved the live
    // ImGui window to. PostDraw looked like the right hook for this, but
    // whatever it actually returned wasn't matching the window's real
    // rect (the glued window ended up frozen in the wrong spot) — possibly
    // it runs after ImGui.End() rather than before, unlike PreDraw/Begin.
    public Vector2 LastPosition { get; private set; }
    public Vector2 LastSize { get; private set; }

    public override void Draw()
    {
        LastPosition = ImGui.GetWindowPos();
        LastSize = ImGui.GetWindowSize();

        // 0 = fill whatever vertical space is left in this window instead
        // of the fixed height StatusWindow uses to keep the list bounded
        // alongside its settings. compact: true drops everything but the
        // refresh button and the results themselves — the category filter
        // lives in StatusWindow (Configuration.AlertCategory), not here.
        _matchListView.Draw(0, compact: true);
    }

    public void Dispose()
    {
    }
}
