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
    /// matches disabled entities takes its place. These queries only find households and citizens, so their copies add only
    /// sleepers. The two city-wide ones, the save and the clear before a load, match sleepers by type (<see cref="WithOwnSleepers"/>).
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
            // Before another city loads, the game's ClearSystem destroys the old one through a query that skips disabled
            // entities: every sleeper survived it and woke into the next city, doubling its households (a player's log, 12
            // November 2026). With sleepers matched, they go with the rest of the old city.
            var clear = world.GetOrCreateSystemManaged<Game.Serialization.ClearSystem>();
            clear.m_ClearQuery = WithOwnSleepers(entities, clear.m_ClearQuery);
        }

        // What this mod puts to sleep: households, their citizens, and the people waiting at crowded stops.
        private static readonly ComponentType[] SleeperTypes =
        {
            ComponentType.ReadOnly<Game.Citizens.Household>(), ComponentType.ReadOnly<Game.Citizens.Citizen>(), ComponentType.ReadOnly<Game.Creatures.Human>(),
        };

        /// <summary>
        /// A copy of a city-wide query that also matches this mod's sleepers, and no other disabled entity. Other mods hide their
        /// own entities with Disabled too: Placemaker keeps its helper areas out of saves that way and rebuilds them at each load.
        /// Saved anyway, they came back without their prefabs, and the game crashed creating from them (two players, 14
        /// November 2026). The original stays as it was, with disabled entities left out, and one small description adds the
        /// disabled entities of the sleeper types.
        /// </summary>
        internal static EntityQuery WithOwnSleepers(EntityManager entities, EntityQuery query)
        {
            var originals = query.GetEntityQueryDescs();
            var descriptions = new List<EntityQueryDesc>();
            // The same options on every description: the originals keep disabled entities out by naming the tag instead.
            var options = EntityQueryOptions.IncludeDisabledEntities;
            foreach (var original in originals) options |= original.Options;
            foreach (var original in originals)
            {
                var none = original.None ?? Array.Empty<ComponentType>();
                descriptions.Add(new EntityQueryDesc
                {
                    All = original.All ?? Array.Empty<ComponentType>(),
                    Any = original.Any ?? Array.Empty<ComponentType>(),
                    None = none.Append(ComponentType.ReadOnly<Disabled>()).ToArray(),
                    Options = options,
                });
            }
            // The sleepers without copying the original's types: the save's query lists every saved component type, hundreds,
            // and copies of that for each sleeper type outgrew the space the game builds a query in (a load with 54 mods came
            // up empty, 14 November 2026). Both city-wide queries match anything with a PrefabRef, which every sleeper has.
            descriptions.Add(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Disabled>() },
                Any = SleeperTypes,
                None = originals.SelectMany(original => original.None ?? Array.Empty<ComponentType>()).Distinct().ToArray(),
                Options = options,
            });
            return entities.CreateEntityQuery(descriptions.ToArray());
        }

        private static bool otherModsApplied;

        /// <summary>
        /// Once, when a city loads and every mod's systems exist: other mods' queries that skipped sleepers and so got the city
        /// wrong get the same copy as the game's (<see cref="OtherModSystems.QueryCountingSleepers"/>). FiscalOverhaul's tax
        /// engine charged them no residential tax (a player's mod list, 11 November 2026); Adult Basic Education's counts of the
        /// city's adults came out far too low (a player's 3-million city, 15 November 2026).
        /// </summary>
        internal static void ApplyToOtherMods(World world)
        {
            if (otherModsApplied) return;
            otherModsApplied = true;
            foreach (var system in world.Systems)
            {
                var type = system.GetType();
                string name = OtherModSystems.QueryCountingSleepers(type.Name);
                if (name == null) continue;
                var field = AccessTools.Field(type, name);
                if (field == null || field.FieldType != typeof(EntityQuery))
                {
                    Mod.Logger.Warn($"{type.FullName} has no {name} query where it used to: it leaves sleeping households out.");
                    continue;
                }
                field.SetValue(system, WithSleepers(world.EntityManager, (EntityQuery)field.GetValue(system)));
                Mod.Logger.Info($"{type.FullName} counts sleeping households too ({name}).");
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
            __instance.m_Query = SleeperBookkeeping.WithOwnSleepers(__instance.EntityManager, __instance.m_Query);
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
