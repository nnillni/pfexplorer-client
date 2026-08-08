using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PfExplorer.Models;

// Mirrors server/src/types.ts IncomingListing — keep both in sync.
public class PfListingDto
{
    [JsonPropertyName("listingId")]
    public string ListingId { get; set; } = string.Empty;

    [JsonPropertyName("contentId")]
    public string ContentId { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("world")]
    public string World { get; set; } = string.Empty;

    [JsonPropertyName("homeWorld")]
    public string HomeWorld { get; set; } = string.Empty;

    [JsonPropertyName("dataCenter")]
    public string DataCenter { get; set; } = string.Empty;

    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    [JsonPropertyName("dutyName")]
    public string DutyName { get; set; } = string.Empty;

    [JsonPropertyName("dutyType")]
    public string DutyType { get; set; } = string.Empty;

    [JsonPropertyName("beginnersWelcome")]
    public bool BeginnersWelcome { get; set; }

    [JsonPropertyName("minItemLevel")]
    public int MinItemLevel { get; set; }

    [JsonPropertyName("parties")]
    public int Parties { get; set; }

    [JsonPropertyName("slotsAvailable")]
    public int SlotsAvailable { get; set; }

    [JsonPropertyName("slotsFilled")]
    public int SlotsFilled { get; set; }

    [JsonPropertyName("secondsRemaining")]
    public int SecondsRemaining { get; set; }

    [JsonPropertyName("jobsPresent")]
    public List<string> JobsPresent { get; set; } = new();

    // One entry per still-open party seat: the list of job abbreviations
    // that seat accepts (empty = no restriction, i.e. any job).
    [JsonPropertyName("openSlotJobs")]
    public List<List<string>> OpenSlotJobs { get; set; } = new();

    // Bracket tags the game itself shows on the listing, e.g. "Loot",
    // "DutyComplete", "OnePlayerPerJob". Raw enum member names.
    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = new();

    [JsonPropertyName("capturedAt")]
    public string CapturedAt { get; set; } = string.Empty;
}

public class IngestPayload
{
    [JsonPropertyName("listings")]
    public List<PfListingDto> Listings { get; set; } = new();

    // Configuration.ContributorId — a random per-install ID, not a
    // character name/account ID. Optional server-side, so omitting it
    // (older builds) doesn't break ingest.
    [JsonPropertyName("contributorId")]
    public string? ContributorId { get; set; }
}

// POST /api/listings' own response body — just enough to read back the
// contributor totals it computed while handling this same request.
public class IngestResponse
{
    [JsonPropertyName("accepted")]
    public int Accepted { get; set; }

    [JsonPropertyName("contributors")]
    public PfContributorStats? Contributors { get; set; }
}

// POST /api/listings/expire's request body — see
// ListingUploader.ExpireAsync and server/src/routes/listings.ts's
// expireSchema.
public class ExpirePayload
{
    [JsonPropertyName("listingIds")]
    public List<string> ListingIds { get; set; } = new();

    [JsonPropertyName("contributorId")]
    public string? ContributorId { get; set; }
}
