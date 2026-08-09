using System;
using Dalamud.Game.Command;
using Dalamud.Game.Gui.PartyFinder.Types;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using PfExplorer.Windows;

namespace PfExplorer;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IPartyFinderGui PartyFinderGui { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IToastGui ToastGui { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;

    private const string CommandName = "/pfexplorer";
    private const string CommandNameShort = "/pf";
    private const string ConfigCommandName = "/pfconfig";

    private readonly Configuration _config;
    private readonly ListingUploader _uploader;
    private readonly AlertPoller _alertPoller;
    private readonly PfScanTracker _scanTracker;
    private readonly MatchListView _matchListView;
    private readonly WindowSystem _windowSystem = new("PfExplorer");
    private readonly StatusWindow _statusWindow;
    private readonly MatchesWindow _matchesWindow;
    private readonly MinimalMatchesWindow _minimalMatchesWindow;

    private bool _wasStatusWindowOpen;

    public Plugin()
    {
        _config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        // Doesn't need character data (unlike TryInitializeDefaults' DC
        // detection below), so this can happen immediately rather than
        // waiting on ObjectTable.LocalPlayer.
        if (string.IsNullOrEmpty(_config.ContributorId))
        {
            _config.ContributorId = Guid.NewGuid().ToString("N");
            _config.Save();
        }

        _uploader = new ListingUploader(_config, Log);
        _alertPoller = new AlertPoller(_config, Log, ChatGui, ToastGui);
        _scanTracker = new PfScanTracker(_alertPoller, _uploader);
        _matchListView = new MatchListView(_config, _alertPoller);

        // MatchesWindow (full) and MinimalMatchesWindow (compact) are the
        // two shapes of the plugin's primary results window (/pfexplorer,
        // "open main UI") — only one open at a time, toggled between via
        // each other's SwitchToMinimal/SwitchToFull titlebar button (see
        // ToggleMinimalView). StatusWindow ("Options") is reached via
        // either one's cog titlebar button or Dalamud's own "Configure"
        // entry. OnOpenOptions/Switch* are wired after all three exist
        // since their constructors would otherwise need each other.
        _matchesWindow = new MatchesWindow(_matchListView);
        _minimalMatchesWindow = new MinimalMatchesWindow(_matchListView);
        _statusWindow = new StatusWindow(_config, _uploader, _alertPoller, _matchesWindow, _minimalMatchesWindow);
        _matchesWindow.OnOpenOptions = () => _statusWindow.Toggle();
        _minimalMatchesWindow.OnOpenOptions = () => _statusWindow.Toggle();
        _matchesWindow.SwitchToMinimal = () => SetMinimalView(true);
        _minimalMatchesWindow.SwitchToFull = () => SetMinimalView(false);

        // Only one of the two starts open, matching whichever mode was
        // last active.
        _matchesWindow.IsOpen = !_config.MatchesWindowMinimal;
        _minimalMatchesWindow.IsOpen = _config.MatchesWindowMinimal;

        // MatchesWindow/MinimalMatchesWindow first so their PostDraw
        // updates LastPosition/LastSize before StatusWindow's PreDraw reads
        // them later this same frame — added the other way around,
        // StatusWindow was always glueing itself to wherever the results
        // window was one frame ago.
        _windowSystem.AddWindow(_matchesWindow);
        _windowSystem.AddWindow(_minimalMatchesWindow);
        _windowSystem.AddWindow(_statusWindow);

        PartyFinderGui.ReceiveListing += OnReceiveListing;

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Toggle the PF Explorer results window.",
        });
        CommandManager.AddHandler(CommandNameShort, new CommandInfo(OnCommand)
        {
            HelpMessage = "Toggle the PF Explorer results window.",
        });
        CommandManager.AddHandler(ConfigCommandName, new CommandInfo(OnConfigCommand)
        {
            HelpMessage = "Open PF Explorer options (opening results too if they're closed).",
        });

        PluginInterface.UiBuilder.Draw += _windowSystem.Draw;
        PluginInterface.UiBuilder.Draw += TryInitializeDefaults;
        PluginInterface.UiBuilder.Draw += _scanTracker.Tick;
        PluginInterface.UiBuilder.Draw += TrackStatusWindowTransitions;
        PluginInterface.UiBuilder.OpenConfigUi += _statusWindow.Toggle;
        PluginInterface.UiBuilder.OpenMainUi += ToggleActiveMatchesWindow;

        Log.Information("PfExplorer loaded.");
    }

    private void OnReceiveListing(IPartyFinderListing listing, IPartyFinderListingEventArgs _)
    {
        var dto = ListingMapper.Map(listing);

        // Fed regardless of the Enabled/private checks below — this feeds
        // both PfScanTracker's stale-match pruning and (via RefreshFromScan
        // just below) the alert pipeline's local ground truth, neither of
        // which are the upload path.
        _scanTracker.NotifyListingReceived(dto);

        // Also unconditional: without this, a listing you were staring at
        // in the native PF window could still get announced "gone" by
        // AlertPoller.PollAsync's server-diff alone. Feeding every listing
        // the game shows you (from you opening/browsing PF yourself — the
        // only source of ReceiveListing events) into RefreshFromScan
        // freshens its CapturedAt/slots and clears local removal
        // suppression.
        var localDataCenter = MatchListView.GetLocalDataCenter();
        if (localDataCenter != null)
            _alertPoller.RefreshFromScan(new[] { dto }, localDataCenter);

        if (!_config.Enabled)
            return;

        // Private listings are only visible to the recruiter's friends/FC —
        // sharing them cross-client defeats the point of "private", so skip
        // them entirely rather than uploading and filtering later.
        if (listing.SearchArea.HasFlag(SearchAreaFlags.Private))
            return;

        _uploader.Enqueue(dto);
    }

    // Whichever of MatchesWindow (full) / MinimalMatchesWindow (compact) is
    // the current mode (Configuration.MatchesWindowMinimal) — commands and
    // OpenMainUi act on this one rather than hardcoding MatchesWindow, so
    // they do the right thing regardless of which shape the results window
    // is currently in.
    private Window ActiveMatchesWindow => _config.MatchesWindowMinimal ? _minimalMatchesWindow : _matchesWindow;

    // Named method rather than a lambda so Dispose can unsubscribe the same
    // delegate instance from OpenMainUi.
    private void ToggleActiveMatchesWindow() => ActiveMatchesWindow.Toggle();

    // Closes the other results window and opens the requested one,
    // persisting the choice — the actual switch logic behind both
    // MatchesWindow.SwitchToMinimal and MinimalMatchesWindow.SwitchToFull.
    private void SetMinimalView(bool minimal)
    {
        if (_config.MatchesWindowMinimal == minimal)
            return;

        _matchesWindow.IsOpen = !minimal;
        _minimalMatchesWindow.IsOpen = minimal;
        _config.MatchesWindowMinimal = minimal;
        _config.Save();
    }

    // Closing results also closes options if it's open — options is glued
    // to results (MatchesWindow/MinimalMatchesWindow) and useless (and
    // visually orphaned) without it, so leaving it open on its own after
    // /pf closes the window it's attached to would just be a stray
    // floating panel. Opening results this way does NOT force options open
    // too — only /pfconfig does that symmetric behavior.
    private void OnCommand(string _, string __)
    {
        var activeWindow = ActiveMatchesWindow;
        var wasOpen = activeWindow.IsOpen;
        activeWindow.Toggle();
        if (wasOpen)
            _statusWindow.IsOpen = false;
    }

    // If results is closed, open both (options is useless without the list
    // it configures). If results is already open, just toggle options on
    // its own — including closing it back down without touching results.
    private void OnConfigCommand(string _, string __)
    {
        var activeWindow = ActiveMatchesWindow;
        if (!activeWindow.IsOpen)
        {
            activeWindow.IsOpen = true;
            _statusWindow.IsOpen = true;
            return;
        }

        _statusWindow.Toggle();
    }

    // Runs every frame regardless of either window's own open state (unlike
    // their Draw/PreDraw, which only fire while IsOpen) — the reliable spot
    // to catch StatusWindow's closed->open transition and tell it to reset
    // to the first tab next time it actually draws.
    private void TrackStatusWindowTransitions()
    {
        if (_statusWindow.IsOpen && !_wasStatusWindowOpen)
            _statusWindow.ForceFirstTabOnNextDraw = true;

        _wasStatusWindowOpen = _statusWindow.IsOpen;
    }

    // Runs every frame (harmless — it's a single bool check) until the
    // player's actually loaded in, since ObjectTable.LocalPlayer is null
    // before then and there's no other reliable "character is ready" signal
    // this early. Seeds AlertDataCenters with your current DC so a fresh
    // install isn't stuck showing "Any" (i.e. everything, every region)
    // until you go configure it yourself.
    private void TryInitializeDefaults()
    {
        if (_config.HasInitializedDefaults)
            return;

        var dataCenter = MatchListView.GetLocalDataCenter();
        if (dataCenter == null)
            return;

        if (_config.AlertDataCenters.Count == 0)
            _config.AlertDataCenters.Add(dataCenter);

        _config.HasInitializedDefaults = true;
        _config.Save();
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= _windowSystem.Draw;
        PluginInterface.UiBuilder.Draw -= TryInitializeDefaults;
        PluginInterface.UiBuilder.Draw -= _scanTracker.Tick;
        PluginInterface.UiBuilder.Draw -= TrackStatusWindowTransitions;
        PluginInterface.UiBuilder.OpenConfigUi -= _statusWindow.Toggle;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleActiveMatchesWindow;
        PartyFinderGui.ReceiveListing -= OnReceiveListing;

        CommandManager.RemoveHandler(CommandName);
        CommandManager.RemoveHandler(CommandNameShort);
        CommandManager.RemoveHandler(ConfigCommandName);

        _windowSystem.RemoveAllWindows();
        _uploader.Dispose();
        _alertPoller.Dispose();
        _matchListView.Dispose();
        _statusWindow.Dispose();
        _matchesWindow.Dispose();
        _minimalMatchesWindow.Dispose();
    }
}
