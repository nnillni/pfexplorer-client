using Dalamud.Configuration;
using System;
using System.Collections.Generic;

namespace PfExplorer;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public bool Enabled { get; set; } = true;

    public string ServerUrl { get; set; } = "https://pfexplorer.com";

    // A random ID this install generates for itself once (see Plugin's
    // constructor) and sends with every upload batch — never a character
    // name or account ID, just enough for the server to count distinct
    // contributing installs ("Contributors: X active").
    public string ContributorId { get; set; } = string.Empty;

    // Polls GET /api/listings and fires a toast when a new listing matches
    // these — independent of Enabled/uploading, since you might want alerts
    // without contributing capture data (or vice versa).
    public bool AlertEnabled { get; set; } = false;

    // Job abbreviations (e.g. "DRG", "WHM") to match against a listing's
    // still-open seats. Empty = any job matches.
    public List<string> AlertJobs { get; set; } = new();

    // 0 = no bound on that side of the range.
    public int AlertIlvlMin { get; set; } = 0;
    public int AlertIlvlMax { get; set; } = 0;

    // Data center names (e.g. "Aether", "Chaos"). Empty = any DC matches.
    public List<string> AlertDataCenters { get; set; } = new();

    // Sent as the server's excludeXivpf query param — hides xivpf.com's
    // broader cross-client feed, showing only listings some Dalamud plugin
    // actually captured. Unlike the server's own XIVPF_SYNC_ENABLED (which
    // turns ingestion off for everyone), this is per-client.
    public bool AlertExcludeXivpf { get; set; } = false;

    // Which category bucket the match list is filtered to (e.g.
    // "BlueMage", "Raids") — defaults to High End Duty since that's
    // generally the highest-signal content to get paged about. Unlike the
    // other Alert* filters this doesn't affect what AlertPoller considers a
    // match, only which of those matches are actually displayed, so it
    // doesn't need AlertPoller.ResetBaseline() when changed. No "show every
    // category" option — CategoryOptions in MatchCategorizer.cs doesn't
    // list one, so this always names a real bucket.
    public string AlertCategory { get; set; } = "HighEndDuty";

    // Which freshness bucket the match list/notifications are filtered to —
    // 0 = green, 1 = yellow, 2 = red (MatchFreshness.Rank), -1 = any.
    // Defaults to green (freshest/most accurate) rather than any. Same
    // display-only nature as AlertCategory: doesn't change what AlertPoller
    // considers a match, just which of those get shown/announced.
    public int AlertFreshness { get; set; } = 0;

    // Which events actually trigger a notification — independent of each
    // other (and of the delivery methods below): a listing you're already
    // watching filling up (5/8 -> 6/8) is a different signal than a brand
    // new one appearing, and either can be turned off on its own. Removed
    // defaults off — new/changed are the actionable ones, gone is mostly
    // noise once new/changed are already the primary signal.
    public bool AlertNotifyOnNewMatch { get; set; } = true;
    public bool AlertNotifyOnPartyChange { get; set; } = true;

    // Fires when a listing that was matching drops out of results —
    // covers the duty ending/filling/being closed, or just aging out of
    // the server's active window, without trying to tell those cases
    // apart (see AlertPoller.AnnounceRemoved).
    public bool AlertNotifyOnRemoved { get; set; } = false;

    // UIColor sheet row IDs used to color each announcement's "<Duty Tab>
    // (tag)" prefix in the chat echo (see AlertPoller.SendNotification) —
    // only matters when AlertNotifyChat is on, but kept independent of it
    // so turning chat off and back on doesn't lose your picks. Defaults
    // picked via StatusWindow's Debug tab color-swatch dump: 45 = green,
    // 502, 518 = vivid red (not 19 — dark red, see AlertPoller's own
    // history of that constant).
    public ushort AlertNewMatchColor { get; set; } = 45;
    public ushort AlertPartyChangeColor { get; set; } = 502;
    public ushort AlertRemovedColor { get; set; } = 518;

    // How to announce: any combination, or none (silent — you'd just
    // notice it in the list/popout on its own). Message format:
    // '<Duty Tab> (new): "Duty Title" by Author (World) (5/8)' for a new
    // match, '...(changed): ... (5/8 -> 6/8)' for a party-size change.
    // Chat echo only by default — toast/sound off.
    public bool AlertNotifyChat { get; set; } = true;
    public bool AlertNotifyToast { get; set; } = false;
    public bool AlertNotifySound { get; set; } = false;

    // Hides each listing's description text in the match list/popout —
    // just the duty/world/recruiter/slots, nothing the recruiter typed.
    public bool AlertHideDescription { get; set; } = false;

    // Which of MatchesWindow (full) / MinimalMatchesWindow (compact — one
    // line per listing, small icon(s) + duty + world/DC, click to open, no
    // search/filter controls) is the active results window — they're two
    // separate Window instances rather than one toggling a content branch,
    // specifically so Dalamud's per-window position/size persistence keeps
    // each one's layout independent (tried the single-window content-branch
    // approach first; it forced both "modes" to share one size/position).
    // Only one is ever open at a time; this is what Plugin uses to decide
    // which on startup and which the SwitchToMinimal/SwitchToFull titlebar
    // buttons toggle between.
    public bool MatchesWindowMinimal { get; set; } = false;

    // Set once, the first time the plugin can actually resolve your
    // character's data center (see Plugin.TryInitializeDefaults) — seeds
    // AlertDataCenters with it so a fresh install isn't stuck on "Any"
    // until you configure something yourself.
    public bool HasInitializedDefaults { get; set; } = false;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
