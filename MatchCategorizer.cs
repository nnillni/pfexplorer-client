using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using PfExplorer.Models;

namespace PfExplorer;

// Category bucketing shared between MatchListView (rendering the tabs/icons)
// and AlertPoller (building the new-match announcement text) — kept out of
// the Windows/ namespace since it's plain matching logic, not UI.
public static class MatchCategorizer
{
    // Same three fights the website's isHighEndDutyListing() reclassifies —
    // xivpf's own category field just tags these "Trials"/"Raids" same as
    // their normal-mode counterparts, so the duty name is the only signal.
    private static readonly HashSet<string> HighEndDutyNames = new()
    {
        "Shinryu's Domain (Unreal)",
        "The Unmaking (Extreme)",
        "AAC Heavyweight M1 (Savage)",
        "AAC Heavyweight M2 (Savage)",
        "AAC Heavyweight M3 (Savage)",
        "AAC Heavyweight M4 (Savage)",
    };

    // Display order, mirroring the website's CATEGORY_ORDER (website/app.js).
    public static readonly string[] CategoryOrder =
    {
        "Roulette", "Dungeons", "GuildQuests", "Trials", "Raids", "HighEndDuty", "FATEs", "TheHunt",
        "GoldSaucer", "GatheringForays", "VCDungeonFinder", "FieldOperations", "OccultCrescent",
        "QuestBattles", "BlueMage",
    };

    private static readonly Dictionary<string, string> CategoryLabels = new()
    {
        ["GuildQuests"] = "Guild Quests",
        ["HighEndDuty"] = "High End Duty",
        ["FATEs"] = "Fates",
        ["TheHunt"] = "The Hunt",
        ["GoldSaucer"] = "Gold Saucer",
        ["GatheringForays"] = "Gathering Forays",
        ["VCDungeonFinder"] = "Variant & Criterion",
        ["FieldOperations"] = "Field Operations",
        ["OccultCrescent"] = "Occult Crescent",
        ["QuestBattles"] = "Quest Battles",
        ["BlueMage"] = "Blue Mage",
        ["TreasureHunts"] = "Treasure Hunts",
    };

    // Raw categories the BLU keyword heuristic (IsBlueMageListing, below)
    // is even allowed to reclassify into the Blue Mage bucket — real duty
    // content only. Started as "anything except FATEs" (FATEs' free-form
    // event chatter constantly false-positives on "BLU"/"spell"), but that
    // still let through GoldSaucer/TreasureHunts/etc content that also
    // isn't a real BLU-farmable duty and just happened to mention "spell"
    // or have a coincidentally BLU-restricted seat (e.g. a FATE train
    // posted under a raw category other than "FATEs" itself). An allow-list
    // of the only content BLU can actually farm for progression/glamour is
    // the correct fix, not chasing more categories to exclude.
    private static readonly HashSet<string> BlueMageEligibleCategories = new()
    {
        "Dungeons", "Trials", "Raids", "HighEndDuty",
    };

    // Same detection as the website's isBlueMageListing() — an explicit ask
    // for a Blue Mage (exclusive/near-exclusive BLU seat, or "BLU"/"spell"
    // mentioned) rather than BLU merely being a legal DPS pick on an
    // unrestricted seat. Without this, xivpf's own category field almost
    // never tags general BLU farm parties "BlueMage" (only its narrow
    // TheMaskedCarnivale content_kind does), so the tab would sit empty.
    public static bool IsBlueMageListing(PfListingSearchResult listing)
    {
        var openCount = Math.Max(0, listing.SlotsAvailable - listing.SlotsFilled);
        if (listing.OpenSlotJobs.Take(openCount).Any(accepted => accepted.Contains("BLU") && accepted.Count <= 2))
            return true;

        return Regex.IsMatch(listing.Description ?? "", @"\bBLU\b", RegexOptions.IgnoreCase)
            || Regex.IsMatch(listing.DutyName ?? "", @"\bBLU\b", RegexOptions.IgnoreCase)
            || Regex.IsMatch(listing.Description ?? "", @"\bspell\b", RegexOptions.IgnoreCase);
    }

    public static string CategoryBucket(PfListingSearchResult listing)
    {
        var rawCategory = string.IsNullOrEmpty(listing.Category) ? "Other" : listing.Category;

        // Two things exclude a listing from the Blue Mage bucket even when
        // xivpf's own category already says "BlueMage" outright (true for
        // the game's built-in Blue Mage content, not just the
        // IsBlueMageListing() keyword guess below):
        //  - World-scoped (SearchAreaFlags.World) listings.
        //  - Masked Carnivale performances — a solo minigame, not real
        //    party recruiting. xivpf tags it "BlueMage" (it genuinely is
        //    Blue Mage content) but every one of those listings has
        //    DutyType "Other" (e.g. "Gentlemen Prefer Swords", "Waiting
        //    for Golem", "The Plant-om of the Opera" — real
        //    recruiting-worthy BLU content uses DutyType "Normal" like
        //    everything else).
        // The keyword guess is only trusted for real BLU-farmable duty
        // content (BlueMageEligibleCategories) — everything else (FATEs,
        // GoldSaucer, TreasureHunts, Other, etc.) has too much free-form
        // chatter/coincidental "BLU"/"spell" mentions to mean an actual
        // Blue Mage ask (e.g. FATE trains: "need BLU for the bomb spell
        // add").
        var isExcludedBlueMage = rawCategory == "BlueMage"
            && (listing.Tags.Contains("World") || listing.DutyType == "Other");
        var isRealBlueMage = !isExcludedBlueMage
            && (rawCategory == "BlueMage" || (BlueMageEligibleCategories.Contains(rawCategory) && !listing.Tags.Contains("World") && IsBlueMageListing(listing)));
        if (isRealBlueMage)
            return "BlueMage";

        if (HighEndDutyNames.Contains(listing.DutyName))
            return "HighEndDuty";

        // rawCategory == "BlueMage" here means it was excluded above (World
        // tag or Masked Carnivale) — there's no more specific bucket to
        // fall back to, so it lands in Other same as any other
        // unclassified listing.
        return rawCategory == "BlueMage" ? "Other" : rawCategory;
    }

    // Same as CategoryBucket, minus the Blue Mage reclassification — the
    // tab a listing genuinely lives under in-game, since the game has no
    // dedicated Blue Mage PF tab: a BLU-flagged dungeon/trial/raid/HET
    // listing still only ever shows (and closes) under its own native
    // Dungeons/Trials/Raids/HighEndDuty tab, same as any other listing in
    // that category. PruneMissing (AlertPoller) matches on this instead of
    // CategoryBucket for exactly that reason — CategoryBucket collapses
    // every BLU-eligible category into one "BlueMage" value, which would
    // make a scan of any single real tab ambiguous about which BLU
    // listings it actually just proved gone.
    public static string NativeBucket(PfListingSearchResult listing)
    {
        var rawCategory = string.IsNullOrEmpty(listing.Category) ? "Other" : listing.Category;
        if (HighEndDutyNames.Contains(listing.DutyName))
            return "HighEndDuty";
        return rawCategory == "BlueMage" ? "Other" : rawCategory;
    }

    public static string CategoryLabel(string category) =>
        CategoryLabels.TryGetValue(category, out var label) ? label : category;

    public static int CategoryOrderIndex(string category)
    {
        var index = Array.IndexOf(CategoryOrder, category);
        return index == -1 ? CategoryOrder.Length : index;
    }

    // One entry per bucket in display order, for StatusWindow's category
    // filter dropdown, plus a leading "All" (empty string) — matches
    // MatchListView's own check (an empty AlertCategory skips the category
    // filter entirely) and the website's equivalent option.
    public static readonly IReadOnlyList<(string Value, string Label)> CategoryOptions =
        new[] { ("", "All") }.Concat(CategoryOrder.Select(c => (c, CategoryLabel(c)))).ToList();

    private static string DutyName(PfListingSearchResult listing) =>
        string.IsNullOrEmpty(listing.DutyName) ? listing.Category : listing.DutyName;

    // Literal substrings AlertPoller looks for to color just the event tag
    // (not the whole announcement) when building the chat echo — must match
    // exactly what appears in the Build*Announcement strings below.
    public const string NewMatchTag = "(new)";
    public const string PartyChangeTag = "(changed)";
    public const string RemovedTag = "(gone)";

    // '<Duty Tab> (new): "Duty Title" by Author (World) (5/8)' — e.g.
    // 'Blue Mage (new): "Leviathan (Extreme)" by Krystal Frost (Excalibur) (5/8)'.
    public static string BuildNewMatchAnnouncement(PfListingSearchResult listing)
    {
        var bucket = CategoryBucket(listing);
        return $"{CategoryLabel(bucket)} {NewMatchTag}: \"{DutyName(listing)}\" by {listing.Name} ({listing.World}) "
            + $"({listing.SlotsFilled}/{listing.SlotsAvailable})";
    }

    // '<Duty Tab> (changed): "Duty Title" by Author (World) (5/8 -> 6/8)'.
    public static string BuildPartyChangeAnnouncement(PfListingSearchResult listing, int previousSlotsFilled)
    {
        var bucket = CategoryBucket(listing);
        return $"{CategoryLabel(bucket)} {PartyChangeTag}: \"{DutyName(listing)}\" by {listing.Name} ({listing.World}) "
            + $"({previousSlotsFilled}/{listing.SlotsAvailable} -> {listing.SlotsFilled}/{listing.SlotsAvailable})";
    }

    // '<Duty Tab> (gone): "Duty Title" by Author (World)' — fired once a
    // previously-matching listing drops out of results. No attempt to
    // distinguish why (party filled, duty started, recruiter closed it,
    // just aged out of the server's active window) — none of that is
    // reliably knowable from here, and "no longer available" covers all of
    // them equally well for the purpose of the notification.
    public static string BuildRemovedAnnouncement(PfListingSearchResult listing)
    {
        var bucket = CategoryBucket(listing);
        return $"{CategoryLabel(bucket)} {RemovedTag}: \"{DutyName(listing)}\" by {listing.Name} ({listing.World})";
    }
}
