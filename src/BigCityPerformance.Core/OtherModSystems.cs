// Big City Performance. Original work by TevBenji, 2026-11. GPL-3.0.
using System.Collections.Generic;

namespace BigCityPerformance.Core
{
    /// <summary>
    /// Some mods turn off a game system and run their own copy, so a patch on the game's system no longer reaches that work.
    /// Matched by class name: no reference to those mods, and their classes don't all share one namespace.
    /// </summary>
    public static class OtherModSystems
    {
        private static readonly HashSet<string> PeopleTrips = new HashSet<string>
        {
            // Realistic Trips.
            "Time2WorkLeisureSystem", "Time2WorkResourceBuyerSystem",
            // MapExt and EconomyEX.
            "FindJobSystemMod", "HouseholdFindPropertySystemMod", "HouseholdFindPropertySystemMod_CellOnly",
            "TripNeededSystemMod", "ResidentAISystemMod", "PersonalCarAISystemMod", "LeisureSystemMod", "FindSchoolSystemMod",
            "ResourceBuyerSystemMod",
        };

        /// <summary>True for another mod's copy of a system that asks for people's own trips.</summary>
        public static bool AsksForPeoplesTrips(string className) => PeopleTrips.Contains(className);

        /// <summary>True for another mod's copy of the system that turns citizens into job seekers (MapExt and EconomyEX).</summary>
        public static bool StartsJobSearches(string className) => className == "CitizenFindJobSystemMod";

        // Queries in other mods' systems that have to see sleepers too, by class name: the field that holds each.
        private static readonly Dictionary<string, string> SleeperQueries = new Dictionary<string, string>
        {
            // FiscalOverhaul: its own residential tax, in place of the game's.
            ["TaxEngineSystem"] = "m_ResidentialTaxPayerGroup",
            // Adult Basic Education: its counts of the city's adults, and where its graduates are six months on.
            ["AdultEducationSystem"] = "m_AllCitizensQuery",
            // Adult Basic Education: the city report it writes to its log.
            ["AdultEducationScanSystem"] = "m_CitizenQuery",
        };

        /// <summary>The field of another mod's query that has to count sleepers too, or null.</summary>
        public static string QueryCountingSleepers(string className) => SleeperQueries.TryGetValue(className, out var field) ? field : null;
    }
}
