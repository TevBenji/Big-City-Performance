// Big City Performance. Original work by TevBenji, 2026-11. GPL-3.0.
using System.Collections.Generic;
using Game;
using Game.Buildings;
using Game.Citizens;
using Game.City;
using Game.Common;
using Game.Creatures;
using Game.Serialization;
using Game.Simulation;
using Game.Tools;
using HarmonyLib;
using Unity.Collections;
using Unity.Entities;
using UnityEngine.Scripting;

namespace BigCityPerformance.People
{
    /// <summary>
    /// Simulates only a share of the city's households in detail; the rest sleep. A sleeping household (and its members) carries
    /// the entity system's Disabled tag, which every system skips, the game's own included: no trips, no searches, no updates.
    /// Its home and its jobs stay taken (buildings and companies still list it), so nobody moves in and the job market isn't
    /// flooded, and it all wakes in an instant. In a million-person city with 90% asleep, the clock ran at about normal speed,
    /// some 20 times faster than without (test runs, 7 November 2026).
    ///
    /// Households go to sleep as their travellers get home; members out on foot sleep where they stand, with their walker.
    /// Anyone riding or waiting for transport keeps the household awake, so no vehicle or stop waits for someone who isn't
    /// there. Everyone wakes before a save (saves stay ordinary, and load without the mod) and sleeps again after.
    ///
    /// What sleeping households would do in bulk is added back: their residential tax goes into the city budget each day.
    /// </summary>
    public sealed partial class DormancySystem : GameSystemBase
    {
        /// <summary>Rendered frames between top-ups: each is one batch of tag changes (a sync point), so not every frame.</summary>
        private const int TopUpFrames = 120;

        // 262,144 simulation frames a day.
        private const uint FramesPerDay = 262144u;

        private readonly List<Entity> sleepingHouseholds = new List<Entity>();
        private readonly HashSet<Entity> sleepingSet = new HashSet<Entity>();
        private readonly List<Entity> sleepingEntities = new List<Entity>();
        private int sleepingCitizens, frames, rawPopulation = -1;
        private uint lastDay = uint.MaxValue;
        private float lastAwakeShare = 1f;
        private EntityQuery households;
        private SimulationSystem simulation;
        private CitySystem citySystem;
        private CityServiceBudgetSystem budget;
        private SaveGameSystem saveGame;
        private EndFrameBarrier endFrame;

        /// <summary>Set by the save patch: everyone is awake until the save is written.</summary>
        internal static bool SaveInProgress;
        internal static DormancySystem Instance;

        internal int SleepingHouseholds => sleepingHouseholds.Count;
        internal int SleepingCitizens => sleepingCitizens;

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            Instance = this;
            simulation = World.GetOrCreateSystemManaged<SimulationSystem>();
            citySystem = World.GetOrCreateSystemManaged<CitySystem>();
            budget = World.GetOrCreateSystemManaged<CityServiceBudgetSystem>();
            saveGame = World.GetOrCreateSystemManaged<SaveGameSystem>();
            endFrame = World.GetOrCreateSystemManaged<EndFrameBarrier>();
            households = GetEntityQuery(ComponentType.ReadOnly<Household>(), ComponentType.ReadOnly<PropertyRenter>(), ComponentType.ReadOnly<HouseholdCitizen>(),
                ComponentType.Exclude<TouristHousehold>(), ComponentType.Exclude<CommuterHousehold>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
        }

        /// <summary>The share of households simulated in detail (1 = everyone, the game as it is).</summary>
        private static float AwakeShare => Mod.Settings == null ? 1f : Unity.Mathematics.math.clamp(Mod.Settings.SimulatedPercent / 100f, 0.05f, 1f);

        [Preserve]
        protected override void OnUpdate()
        {
            if (SaveInProgress)
            {
                // The save patch woke everyone; sleep again once the game has written the save.
                if (saveGame.Enabled) return;
                SaveInProgress = false;
            }
            if (++frames < TopUpFrames) return;
            frames = 0;
            float awake = AwakeShare;
            // Wake people only when the setting goes up. The target also drifts down a little whenever awake households move
            // away; waking everyone for that (then putting them back to sleep) churned the whole city (test run, 7 November 2026).
            if (awake > lastAwakeShare && sleepingHouseholds.Count > 0) WakeAll();
            lastAwakeShare = awake;
            int total = households.CalculateEntityCount() + sleepingHouseholds.Count;
            int wanted = (int)(total * (1f - awake));
            if (wanted > sleepingHouseholds.Count) Sleep(wanted - sleepingHouseholds.Count);
            AddTaxes();
        }

        private void Sleep(int count)
        {
            var batch = new List<Entity>();
            var walkers = new List<Entity>();
            using (var found = households.ToEntityArray(Allocator.Temp))
            {
                // An even stride over the awake households, so the ones still awake are spread over the whole city.
                int stride = System.Math.Max(1, found.Length / System.Math.Max(1, count));
                for (int start = 0; start < stride && count > 0; start++)
                    for (int index = start; index < found.Length && count > 0; index += stride)
                    {
                        var household = found[index];
                        if (sleepingSet.Contains(household)) continue;
                        var members = EntityManager.GetBuffer<HouseholdCitizen>(household, true);
                        walkers.Clear();
                        if (!AllAtHomeOrOnFoot(members, walkers)) continue;
                        batch.Add(household);
                        for (int at = 0; at < members.Length; at++) batch.Add(members[at].m_Citizen);
                        batch.AddRange(walkers);
                        sleepingCitizens += members.Length;
                        sleepingHouseholds.Add(household);
                        sleepingSet.Add(household);
                        count--;
                    }
            }
            if (batch.Count == 0) return;
            sleepingEntities.AddRange(batch);
            // Through the game's end-of-frame command buffer, as its own systems change entities: never in the middle of a frame,
            // while its jobs may still be reading them (doing it directly crashed the game, test run 7 November 2026).
            var commands = endFrame.CreateCommandBuffer();
            using (var array = new NativeArray<Entity>(batch.ToArray(), Allocator.Temp)) commands.AddComponent<Disabled>(array);
        }

        /// <summary>True when every member is home or out on foot (their walkers listed); false if anyone rides or waits for transport.</summary>
        private bool AllAtHomeOrOnFoot(DynamicBuffer<HouseholdCitizen> members, List<Entity> walkers)
        {
            for (int at = 0; at < members.Length; at++)
            {
                var citizen = members[at].m_Citizen;
                if (!EntityManager.HasComponent<CurrentTransport>(citizen)) continue;
                var walker = EntityManager.GetComponentData<CurrentTransport>(citizen).m_CurrentTransport;
                if (!EntityManager.Exists(walker) || !EntityManager.HasComponent<Human>(walker) || !EntityManager.HasComponent<Resident>(walker)) return false;
                var flags = EntityManager.GetComponentData<Resident>(walker).m_Flags;
                if (EntityManager.HasComponent<CurrentVehicle>(walker)
                    || (flags & (ResidentFlags.InVehicle | ResidentFlags.WaitingTransport | ResidentFlags.Disembarking)) != 0
                    || (EntityManager.GetComponentData<Human>(walker).m_Flags & HumanFlags.Waiting) != 0) return false;
                walkers.Add(walker);
            }
            return true;
        }

        /// <summary>Everyone awake: before a save, when the setting goes back up, or when the mod unloads.</summary>
        internal void WakeAll()
        {
            if (sleepingEntities.Count == 0) return;
            var alive = new List<Entity>(sleepingEntities.Count);
            foreach (var entity in sleepingEntities) if (EntityManager.Exists(entity)) alive.Add(entity);
            if (SaveInProgress)
                // The save starts now, in this frame: wake them at once, before anything is written.
                using (var array = new NativeArray<Entity>(alive.ToArray(), Allocator.Temp)) EntityManager.RemoveComponent<Disabled>(array);
            else
            {
                var commands = endFrame.CreateCommandBuffer();
                using (var array = new NativeArray<Entity>(alive.ToArray(), Allocator.Temp)) commands.RemoveComponent<Disabled>(array);
            }
            sleepingEntities.Clear();
            sleepingHouseholds.Clear();
            sleepingSet.Clear();
            sleepingCitizens = 0;
            RestorePopulation();
        }

        /// <summary>
        /// The game recounts the population (CountHouseholdDataSystem) without the sleepers; add them back, so the display and
        /// the game's rules for a city this size still see everyone.
        /// </summary>
        private void KeepPopulation()
        {
            var city = citySystem.City;
            if (city == Entity.Null || !EntityManager.HasComponent<Game.City.Population>(city)) return;
            var population = EntityManager.GetComponentData<Game.City.Population>(city);
            int counted = population.m_Population;
            // Already added since the game's last count.
            if (rawPopulation >= 0 && counted == rawPopulation + sleepingCitizens) return;
            rawPopulation = counted;
            if (sleepingCitizens == 0) return;
            population.m_Population = counted + sleepingCitizens;
            population.m_PopulationWithMoveIn += sleepingCitizens;
            EntityManager.SetComponentData(city, population);
        }

        private void RestorePopulation() => rawPopulation = -1;

        /// <summary>Once a game day: the sleeping households' residential tax, at the awake households' rate per household.</summary>
        private void AddTaxes()
        {
            uint day = simulation.frameIndex / FramesPerDay;
            if (day == lastDay) return;
            bool first = lastDay == uint.MaxValue;
            lastDay = day;
            int awake = households.CalculateEntityCount();
            if (first || awake == 0 || sleepingHouseholds.Count == 0) return;
            var city = citySystem.City;
            if (city == Entity.Null || !EntityManager.HasComponent<PlayerMoney>(city)) return;
            long monthly = budget.GetIncome(IncomeSource.TaxResidential);
            if (monthly <= 0) return;
            long owed = monthly * sleepingHouseholds.Count / awake;
            var money = EntityManager.GetComponentData<PlayerMoney>(city);
            money.Add((int)System.Math.Min(owed, int.MaxValue));
            EntityManager.SetComponentData(city, money);
        }

        [Preserve]
        protected override void OnGameLoadingComplete(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            base.OnGameLoadingComplete(purpose, mode);
            // A freshly loaded city has nobody asleep (saves are written awake).
            sleepingEntities.Clear();
            sleepingHouseholds.Clear();
            sleepingSet.Clear();
            sleepingCitizens = 0;
            rawPopulation = -1;
            lastDay = uint.MaxValue;
            lastAwakeShare = 1f;
            frames = 0;
        }
    }

    /// <summary>Everyone wakes as a save starts, so the save holds the city as the game knows it.</summary>
    [HarmonyPatch(typeof(SaveGameSystem), nameof(SaveGameSystem.RunOnce))]
    internal static class SaveWakePatch
    {
        private static void Prefix()
        {
            DormancySystem.SaveInProgress = true;
            DormancySystem.Instance?.WakeAll();
        }
    }
}
