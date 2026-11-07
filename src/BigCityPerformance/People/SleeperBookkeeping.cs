// Big City Performance. Original work by TevBenji, 2026-11. GPL-3.0.
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

        private static EntityQuery WithSleepers(EntityManager entities, EntityQuery query)
        {
            var descriptions = query.GetEntityQueryDescs();
            foreach (var description in descriptions) description.Options |= EntityQueryOptions.IncludeDisabledEntities;
            return entities.CreateEntityQuery(descriptions);
        }
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
