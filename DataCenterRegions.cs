using System;
using System.Linq;

namespace PfExplorer;

// Shared between StatusWindow (DC filter checkboxes) and MatchListView (the
// Travel button's same-region gate) — one list instead of two copies
// drifting apart. Same grouping as the website's DC_REGIONS.
public static class DataCenterRegions
{
    public static readonly (string Region, string[] DataCenters)[] All =
    {
        ("NA", new[] { "Aether", "Crystal", "Dynamis", "Primal" }),
        ("EU", new[] { "Chaos", "Light" }),
        ("JP", new[] { "Elemental", "Gaia", "Mana", "Meteor" }),
        ("OCE", new[] { "Materia" }),
    };

    public static string? RegionOf(string? dataCenter)
    {
        if (string.IsNullOrEmpty(dataCenter))
            return null;

        foreach (var (region, dataCenters) in All)
        {
            if (dataCenters.Contains(dataCenter, StringComparer.OrdinalIgnoreCase))
                return region;
        }

        return null;
    }
}
