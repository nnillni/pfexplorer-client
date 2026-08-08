using System;
using System.Globalization;
using System.Numerics;

namespace PfExplorer;

// Same freshness bucketing as the website's freshnessRank (website/app.js) —
// how long since a plugin/xivpf last reconfirmed a listing. Shared between
// MatchListView (row tint + the freshness filter dropdown) and AlertPoller
// (so a notification only fires for a match that's actually green/fresh
// when you've filtered down to that).
public static class MatchFreshness
{
    private const double GreenMinutes = 3;
    private const double YellowMinutes = 10;

    public static readonly string[] Labels = { "Green (<3min)", "Yellow (<10min)", "Red (>10min)" };

    // Opaque text colors, shared by StatusWindow's freshness dropdown and
    // MatchListView's per-bucket result counts — index matches Rank()'s
    // return value (0/1/2). Same hues as the row-tint backgrounds
    // (MatchListView's FreshnessGreenBg/YellowBg/RedBg), just full alpha
    // since these sit on plain text instead of a whole row.
    public static readonly Vector4[] Colors =
    {
        new(0.44f, 0.75f, 0.55f, 1f), // green
        new(0.85f, 0.72f, 0.47f, 1f), // yellow
        new(0.88f, 0.41f, 0.37f, 1f), // red
    };

    // 0 = green (<3min since last reconfirmed), 1 = yellow (<10min), 2 = red.
    public static int Rank(string capturedAt)
    {
        // No/unparseable timestamp shouldn't be treated as fresh.
        if (!DateTime.TryParse(capturedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var captured))
            return 2;

        var elapsedMinutes = (DateTime.UtcNow - captured.ToUniversalTime()).TotalMinutes;
        if (elapsedMinutes < GreenMinutes)
            return 0;
        return elapsedMinutes < YellowMinutes ? 1 : 2;
    }
}
