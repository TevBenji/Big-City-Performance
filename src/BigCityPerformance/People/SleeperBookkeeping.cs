// Big City Performance. Original work by TevBenji, 2026-11. GPL-3.0.
using BigCityPerformance.Core;
using Game.Simulation;
using HarmonyLib;
using Unity.Entities;

namespace BigCityPerformance.People
{
    /// <summary>
    /// The game's own bookkeeping keeps covering sleeping households, so the city adds up as if everyone were awake: the
    /// population count (and with it the workforce, education, ages and what households need from shops, which drive demand),
    /// wages and residential tax. Each of those systems finds its households through a query; a copy of that query that also
    /// matches disabled entities takes its place. The game never disables anything itself, so with nobody asleep the copies
    /// match exactly what the originals did.
    /// </summary>
    internal static class SleeperBookkeeping
    {
        internal static void Apply(World world)
        {
            var entities = world.EntityManager;
            var count = world.GetOrCreateSystemManaged<CountHouseholdDataSystem>();
            count.m_HouseholdQuery = WithSleepers(entities, count.m_HouseholdQuery);
            count.m_NotHomelessHouseholdQuery = WithSleepers(entities, count.m_NotHomelessHouseholdQuery);
            count.m_HomelessHouseholdQuery = WithSleepers(entities, count.m_HomelessHouseholdQuery);
            count.m_CitizensQuery = WithSleepers(entities, count.m_CitizensQuery);
            var wages = world.GetOrCreateSystemManaged<PayWageSystem>();
            wages.m_HouseholdGroup = WithSleepers(entities, wages.m_HouseholdGroup);
            var tax = world.GetOrCreateSystemManaged<TaxSystem>();
            tax.m_ResidentialTaxPayerGroup = WithSleepers(entities, tax.m_ResidentialTaxPayerGroup);
        }

        private static bool otherModsApplied;

        /// <summary>
        /// Once, when a city loads and every mod's systems exist: a mod that charges tax in place of the game's TaxSystem
        /// (FiscalOverhaul's tax engine) has its own residential query, which skipped sleepers, so with both mods on they paid
        /// no residential tax (a player's mod list, 11 November 2026). Its query gets the same copy as the game's.
        /// </summary>
        internal static void ApplyToOtherMods(World world)
        {
            if (otherModsApplied) return;
            otherModsApplied = true;
            foreach (var system in world.Systems)
            {
                var type = system.GetType();
                if (!OtherModSystems.ChargesResidentialTax(type.Name)) continue;
                var field = AccessTools.Field(type, "m_ResidentialTaxPayerGroup");
                if (field == null || field.FieldType != typeof(EntityQuery))
                {
                    Mod.Logger.Warn($"{type.FullName} has no residential tax query where it used to: sleeping households pay it no residential tax.");
                    continue;
                }
                field.SetValue(system, WithSleepers(world.EntityManager, (EntityQuery)field.GetValue(system)));
                Mod.Logger.Info($"{type.FullName} charges sleeping households residential tax too.");
            }
        }

        internal static EntityQuery WithSleepers(EntityManager entities, EntityQuery query)
        {
            var descriptions = query.GetEntityQueryDescs();
            foreach (var description in descriptions) description.Options |= EntityQueryOptions.IncludeDisabledEntities;
            return entities.CreateEntityQuery(descriptions);
        }
    }

    /// <summary>
    /// The save writer finds what to write through one query, and on its own that query skips disabled entities: sleepers
    /// would be lost. With sleepers matched too, they are written without waking anyone, and as ordinary households, because
    /// the writer drops any tag it has no serializer for and Disabled has none.
    /// </summary>
    [HarmonyPatch(typeof(Game.Serialization.SerializerSystem), "CreateQuery")]
    internal static class SaveQueryPatch
    {
        /// <summary>False if a game update moved this method: saves then wake everyone first, as they did before.</summary>
        internal static bool Applied;

        private static void Postfix(Game.Serialization.SerializerSystem __instance) =>
            __instance.m_Query = SleeperBookkeeping.WithSleepers(__instance.EntityManager, __instance.m_Query);
    }

    /// <summary>
    /// Counting the whole city, sleepers included, is the costliest single update of a step (about 47 ms of work on a million
    /// people; test run, 7 November 2026), and sleepers don't change. While anyone sleeps it runs one time in four: every 64
    /// frames instead of 16, still several times a game hour.
    /// </summary>
    [HarmonyPatch(typeof(CountHouseholdDataSystem), "OnUpdate")]
    internal static class CountPace
    {
        private static int calls;

        private static bool Prefix() => (DormancySystem.Instance?.SleepingHouseholds ?? 0) == 0 || ++calls % 4 == 0;
    }
}
