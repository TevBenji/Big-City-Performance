// Big City Performance. Original work by TevBenji, 2026-11. GPL-3.0.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BigCityPerformance.Core;
using Game;
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
    /// The city-wide counts each walk the whole city every 16 frames and start their totals over each time: every household
    /// and citizen, sleepers included (about 47 ms of work per call on a million people; test run, 7 November 2026), every
    /// company (about 9 ms per call on a million, 150 ms on three million) and every home (about 5 and 58 ms; test runs, 9 and
    /// 12 November 2026). They feed demand and the statistics, which don't need a fresh count four times a second, and
    /// sleepers don't change. While anyone sleeps each runs one time in four: every 64 frames instead of 16, still several
    /// times a game hour. A skipped update keeps the last whole count. The first update after a city loads always runs: the
    /// company count starts from zero production.
    /// </summary>
    [HarmonyPatch]
    internal static class CountPace
    {
        internal const int Every = 4;

        /// <summary>The game's own pace, for the test runner's comparisons.</summary>
        internal static bool DevOff = false;

        private static readonly Dictionary<Type, int> Calls = new Dictionary<Type, int>();

        private static IEnumerable<MethodBase> TargetMethods() => new[] { typeof(CountHouseholdDataSystem), typeof(CountCompanyDataSystem), typeof(CountResidentialPropertySystem) }
            .Select(type => (MethodBase)AccessTools.DeclaredMethod(type, "OnUpdate"));

        /// <summary>A city loaded: each count's next update runs.</summary>
        internal static void Reset() => Calls.Clear();

        private static bool Prefix(GameSystemBase __instance)
        {
            if (DevOff || (DormancySystem.Instance?.SleepingHouseholds ?? 0) == 0) return true;
            var type = __instance.GetType();
            Calls.TryGetValue(type, out int calls);
            Calls[type] = calls + 1;
            return calls % Every == 0;
        }
    }
}
