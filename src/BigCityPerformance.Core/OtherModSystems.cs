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

        /// <summary>True for another mod's copy of the game's tax charge, with a residential query of its own (FiscalOverhaul).</summary>
        public static bool ChargesResidentialTax(string className) => className == "TaxEngineSystem";
    }
}
