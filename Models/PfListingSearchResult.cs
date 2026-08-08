using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PfExplorer.Models;

// Mirrors the subset of server/src/routes/listings.ts rowToJson() that
// AlertPoller actually needs to match against and announce — not every
// field GET /api/listings returns.
public class PfListingSearchResult
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    // The game's own raw Party Finder listing ID (not our DB row's `id`
    // above) — real for both plugin- and xivpf-sourced rows (xivpfSync.ts
    // passes the real listing id through), so it's what
    // AgentLookingForGroup.OpenListing needs to pull up this exact listing
    // in the native PF window regardless of which source captured it.
    [JsonPropertyName("listingId")]
    public string ListingId { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("world")]
    public string World { get; set; } = string.Empty;

    [JsonPropertyName("dataCenter")]
    public string DataCenter { get; set; } = string.Empty;

    [JsonPropertyName("dutyName")]
    public string DutyName { get; set; } = string.Empty;

    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    // "Normal" for real recruiting duties, "Other" for Blue Mage's Masked
    // Carnivale (a solo performance minigame the game still exposes through
    // PF, not real party recruiting) — see MatchCategorizer.CategoryBucket.
    [JsonPropertyName("dutyType")]
    public string DutyType { get; set; } = string.Empty;

    [JsonPropertyName("minItemLevel")]
    public int MinItemLevel { get; set; }

    // ISO timestamp of when a plugin/xivpf last reconfirmed this listing —
    // the same freshness signal the website's row tint and sort order use.
    [JsonPropertyName("capturedAt")]
    public string CapturedAt { get; set; } = string.Empty;

    [JsonPropertyName("slotsAvailable")]
    public int SlotsAvailable { get; set; }

    [JsonPropertyName("slotsFilled")]
    public int SlotsFilled { get; set; }

    // Jobs already in the party (filled seats).
    [JsonPropertyName("jobsPresent")]
    public List<string> JobsPresent { get; set; } = new();

    // Bracket tags the game itself shows on the listing, e.g. "Loot",
    // "DutyComplete", "OnePlayerPerJob" — raw enum member names, same as
    // the website's TAG_META keys.
    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = new();

    // One entry per still-open party seat: the list of job abbreviations
    // that seat accepts (empty = no restriction, i.e. any job) — same shape
    // as PfListingDto.OpenSlotJobs on the way out.
    [JsonPropertyName("openSlotJobs")]
    public List<List<string>> OpenSlotJobs { get; set; } = new();
}

public class PfListingSearchResponse
{
    [JsonPropertyName("listings")]
    public List<PfListingSearchResult> Listings { get; set; } = new();

    [JsonPropertyName("contributors")]
    public PfContributorStats? Contributors { get; set; }
}

// Total distinct contributor IDs the server's ever seen, and how many
// showed up in an upload within the last 15 minutes (server's
// CONTRIBUTOR_ACTIVE_MINUTES) — see Configuration.ContributorId for what a
// "contributor" actually is.
public class PfContributorStats
{
    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("active")]
    public int Active { get; set; }
}
