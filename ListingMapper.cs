using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Gui.PartyFinder.Types;
using PfExplorer.Models;

namespace PfExplorer;

// Turns a raw game-provided IPartyFinderListing into the plain DTO we ship
// to the server. Isolated in its own class because this is the part most
// exposed to Dalamud/Lumina API drift across versions — if a field here
// breaks after a Dalamud update, this is the file to fix.
public static class ListingMapper
{
    // Job abbreviation per flag. Classification (specific job vs. full role
    // vs. a limited subset like "SGE/SCH only") happens on the website,
    // which needs the raw accepted-job list to tell those apart — collapsing
    // to a role here would lose exactly that distinction.
    private static readonly Dictionary<JobFlags, string> AbbrByJob = new()
    {
        [JobFlags.Gladiator] = "GLA",
        [JobFlags.Marauder] = "MRD",
        [JobFlags.Paladin] = "PLD",
        [JobFlags.Warrior] = "WAR",
        [JobFlags.DarkKnight] = "DRK",
        [JobFlags.Gunbreaker] = "GNB",
        [JobFlags.Conjurer] = "CNJ",
        [JobFlags.WhiteMage] = "WHM",
        [JobFlags.Scholar] = "SCH",
        [JobFlags.Astrologian] = "AST",
        [JobFlags.Sage] = "SGE",
        [JobFlags.Pugilist] = "PGL",
        [JobFlags.Lancer] = "LNC",
        [JobFlags.Archer] = "ARC",
        [JobFlags.Thaumaturge] = "THM",
        [JobFlags.Arcanist] = "ACN",
        [JobFlags.Rogue] = "ROG",
        [JobFlags.Monk] = "MNK",
        [JobFlags.Dragoon] = "DRG",
        [JobFlags.Bard] = "BRD",
        [JobFlags.BlackMage] = "BLM",
        [JobFlags.Summoner] = "SMN",
        [JobFlags.Ninja] = "NIN",
        [JobFlags.Machinist] = "MCH",
        [JobFlags.Samurai] = "SAM",
        [JobFlags.RedMage] = "RDM",
        [JobFlags.BlueMage] = "BLU",
        [JobFlags.Dancer] = "DNC",
        [JobFlags.Reaper] = "RPR",
        [JobFlags.Viper] = "VPR",
        [JobFlags.Pictomancer] = "PCT",
    };

    public static PfListingDto Map(IPartyFinderListing listing)
    {
        var world = SafeSheetText(() => listing.CurrentWorld.ValueNullable?.Name.ExtractText());
        var homeWorld = SafeSheetText(() => listing.HomeWorld.ValueNullable?.Name.ExtractText());
        var dataCenter = SafeSheetText(() =>
            listing.CurrentWorld.ValueNullable?.DataCenter.ValueNullable?.Name.ExtractText());
        // For a Duty Roulette listing, RawDuty is NOT a ContentFinderCondition
        // row ID — it's a row ID into the separate ContentRoulette sheet. Duty
        // above blindly resolves RawDuty against ContentFinderCondition
        // regardless of DutyType, so for roulettes it silently returns
        // whichever CFC row happens to share that ID (e.g. a low roulette ID
        // landing on an unrelated early dungeon like Tam-Tara Deepcroft) —
        // wrong, but not null, so it doesn't fall through to a sane fallback.
        // Skip it here so dutyName stays empty and the website falls back to
        // showing the category label ("Roulette") instead of a bogus duty.
        var dutyName = listing.DutyType == DutyType.Roulette
            ? string.Empty
            : SafeSheetText(() => listing.Duty.ValueNullable?.Name.ExtractText());
        if (string.IsNullOrEmpty(dutyName) && listing.Category == DutyCategory.FATEs)
        {
            // FATE listings aren't tied to a ContentFinderCondition row, so
            // Duty above resolves to nothing — RawDuty is a row ID into the
            // Fate sheet instead, whose Name is the FATE's name (what the
            // game itself shows in place of a duty name for these).
            dutyName = SafeSheetText(() =>
                Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Fate>()?.GetRowOrDefault(listing.RawDuty)?.Name.ExtractText());
        }

        var jobsPresent = listing.JobsPresent
            .Select(job => SafeSheetText(() => job.ValueNullable?.Abbreviation.ExtractText()))
            .Where(abbr => !string.IsNullOrEmpty(abbr))
            .ToList();

        var openSlotJobs = GetOpenSlotJobs(listing);
        var tags = GetTags(listing);

        return new PfListingDto
        {
            ListingId = listing.Id.ToString(),
            // Hashed, not the raw character content ID — see
            // IngestSigning.HashContentId for why.
            ContentId = IngestSigning.HashContentId(listing.ContentId.ToString()),
            Name = listing.Name.TextValue,
            Description = listing.Description.TextValue,
            World = world,
            HomeWorld = homeWorld,
            DataCenter = dataCenter,
            Category = listing.Category.ToString(),
            DutyName = dutyName,
            DutyType = listing.DutyType.ToString(),
            BeginnersWelcome = listing.BeginnersWelcome,
            MinItemLevel = listing.MinimumItemLevel,
            Parties = listing.Parties,
            SlotsAvailable = listing.SlotsAvailable,
            SlotsFilled = listing.SlotsFilled,
            SecondsRemaining = listing.SecondsRemaining,
            JobsPresent = jobsPresent,
            OpenSlotJobs = openSlotJobs,
            Tags = tags,
            CapturedAt = DateTime.UtcNow.ToString("o"),
        };
    }

    // listing.Slots always has 8 entries regardless of the duty's actual
    // party size (a 4-person dungeon still reports 8, with the extra 4 as
    // padding) — one entry per party seat (filled and open) with the jobs
    // it's accepting. JobsPresent — the jobs already in the party — lines
    // up with the first SlotsFilled seats, so skipping that many gets past
    // the filled ones, and taking only (SlotsAvailable - SlotsFilled) more
    // trims off the padding beyond the duty's real capacity.
    private static List<List<string>> GetOpenSlotJobs(IPartyFinderListing listing)
    {
        try
        {
            var openCount = Math.Max(0, listing.SlotsAvailable - listing.SlotsFilled);
            return listing.Slots
                .Skip(listing.SlotsFilled)
                .Take(openCount)
                .Select(slot => slot.Accepting
                    .Select(job => AbbrByJob.TryGetValue(job, out var abbr) ? abbr : null)
                    .Where(abbr => abbr != null)
                    .Select(abbr => abbr!)
                    .Distinct()
                    .ToList())
                .ToList();
        }
        catch
        {
            return new List<List<string>>();
        }
    }

    // The bracket tags the game itself shows on a PF listing ("[Loot]",
    // "[Duty Complete]", "[One Player per Job]", etc.) — raw enum member
    // names, same pattern as Category: the website owns display labels and
    // colors so tag styling doesn't need a plugin rebuild to tweak.
    private static List<string> GetTags(IPartyFinderListing listing)
    {
        var tags = new List<string>();

        if (listing.Objective.HasFlag(ObjectiveFlags.DutyCompletion)) tags.Add("DutyCompletion");
        if (listing.Objective.HasFlag(ObjectiveFlags.Practice)) tags.Add("Practice");
        if (listing.Objective.HasFlag(ObjectiveFlags.Loot)) tags.Add("Loot");

        if (listing.Conditions.HasFlag(ConditionFlags.DutyComplete)) tags.Add("DutyComplete");
        if (listing.Conditions.HasFlag(ConditionFlags.DutyIncomplete)) tags.Add("DutyIncomplete");
        if (listing.Conditions.HasFlag(ConditionFlags.DutyCompleteWeeklyUnclaimed)) tags.Add("DutyCompleteWeeklyUnclaimed");

        if (listing.SearchArea.HasFlag(SearchAreaFlags.OnePlayerPerJob)) tags.Add("OnePlayerPerJob");
        if (listing.SearchArea.HasFlag(SearchAreaFlags.World)) tags.Add("World");

        return tags;
    }

    private static string SafeSheetText(Func<string?> accessor)
    {
        try
        {
            return accessor() ?? string.Empty;
        }
        catch
        {
            // Excel sheet lookups can throw if a RowRef points at an
            // unloaded/invalid row; never let that take the whole batch down.
            return string.Empty;
        }
    }
}
