// Big City Performance. Original work by TevBenji, 2026-11. GPL-3.0.
using System.Collections.Generic;
using Game;
using Game.Buildings;
using Game.Citizens;
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
    /// The game's own counts, wages and residential tax still cover sleepers (<see cref="SleeperBookkeeping"/>).
    ///
    /// Sleepers take turns: the ones asleep longest wake a few at a time, and the next top-up puts as many others to sleep, so
    /// every household gets its time awake (growing older, changing jobs, moving) about once every <see cref="RotationDays"/>
    /// game days, while the number awake stays at the limit.
    /// </summary>
    public sealed partial class DormancySystem : GameSystemBase
    {
        /// <summary>Rendered frames between top-ups: each is one batch of tag changes (a sync point), so not every frame.</summary>
        private const int TopUpFrames = 120;

        /// <summary>Game days for every sleeper to have had a turn awake.</summary>
        internal static float RotationDays = 2f;

        // A game day is 262,144 simulation frames.
        private const float FramesPerDay = 262144f;

        private readonly List<Entity> sleepingHouseholds = new List<Entity>();
        private readonly HashSet<Entity> sleepingSet = new HashSet<Entity>();
        private int sleepingCitizens, frames;
        private int lastLimit = -1;
        private bool afterSave;
        private EntityQuery households, waiting, asleep;

        // Only in a fully loaded city in play: sleeping people while a city loads lost track of them when loading finished
        // (test run, 7 November 2026).
        private bool ready;
        private readonly StopCrowds crowds = new StopCrowds();
        private SaveGameSystem saveGame;
        private CitySystem citySystem;
        private EndFrameBarrier endFrame;
        private SimulationSystem simulation;
        private uint lastRotation;
        private double rotationDue;

        /// <summary>Set by the save patch: everyone is awake until the save is written.</summary>
        internal static bool SaveInProgress;

        // The last entity woken; once it's awake, the whole batch is (one command buffer).
        private Entity wakeProbe;

        /// <summary>True once everyone woken has had the tag taken off.</summary>
        internal bool Awake => wakeProbe == Entity.Null || !EntityManager.Exists(wakeProbe) || !EntityManager.HasComponent<Disabled>(wakeProbe);
        internal static DormancySystem Instance;

        internal int SleepingHouseholds => sleepingHouseholds.Count;
        internal int SleepingCitizens => sleepingCitizens;
        internal int SleepingAtStops => crowds.Asleep;
        internal IReadOnlyList<Entity> SleepingHouseholdList => sleepingHouseholds;

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            Instance = this;
            saveGame = World.GetOrCreateSystemManaged<SaveGameSystem>();
            endFrame = World.GetOrCreateSystemManaged<EndFrameBarrier>();
            citySystem = World.GetOrCreateSystemManaged<CitySystem>();
            simulation = World.GetOrCreateSystemManaged<SimulationSystem>();
            households = GetEntityQuery(ComponentType.ReadOnly<Household>(), ComponentType.ReadOnly<PropertyRenter>(), ComponentType.ReadOnly<HouseholdCitizen>(),
                ComponentType.Exclude<TouristHousehold>(), ComponentType.Exclude<CommuterHousehold>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
            waiting = GetEntityQuery(ComponentType.ReadOnly<Human>(), ComponentType.ReadOnly<Resident>(), ComponentType.ReadOnly<HumanCurrentLane>(),
                ComponentType.Exclude<CurrentVehicle>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
            asleep = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Disabled>() },
                Any = new[] { ComponentType.ReadOnly<Household>(), ComponentType.ReadOnly<Citizen>(), ComponentType.ReadOnly<Human>() },
                Options = EntityQueryOptions.IncludeDisabledEntities,
            });
            SleeperBookkeeping.Apply(World);
        }

        /// <summary>The most people simulated in detail; 0 = everyone (the game as it is).</summary>
        private static int Limit => Mod.Settings == null || !Mod.Settings.LimitPeople ? 0 : Mod.Settings.PeopleInDetail * 1000;

        /// <summary>The city's population as the game counts it, sleepers included (see <see cref="SleeperBookkeeping"/>).</summary>
        private int Population()
        {
            var city = citySystem.City;
            return city != Entity.Null && EntityManager.HasComponent<Game.City.Population>(city) ? EntityManager.GetComponentData<Game.City.Population>(city).m_Population : 0;
        }

        [Preserve]
        protected override void OnUpdate()
        {
            if (SaveInProgress)
            {
                // The save patch woke everyone; sleep again once the game has written the save, at once: they're still at home.
                if (saveGame.Enabled) return;
                SaveInProgress = false;
                frames = TopUpFrames;
                afterSave = true;
            }
            if (!ready || ++frames < TopUpFrames) return;
            frames = 0;
            bool justSaved = afterSave;
            afterSave = false;
            int limit = Limit;
            // Everyone wakes when the setting changes, and the next top-up sleeps the right number. Drift as the city grows or
            // shrinks only ever adds sleepers: waking everyone for it churned the whole city (test run, 7 November 2026).
            if (limit != lastLimit && sleepingHouseholds.Count > 0) WakeAll();
            lastLimit = limit;
            int people = Population();
            if (limit == 0 || people <= limit) return;
            float awake = (float)limit / people;
            int total = households.CalculateEntityCount() + sleepingHouseholds.Count;
            int wanted = (int)(total * (1f - awake));
            Rotate();
            if (wanted > sleepingHouseholds.Count) Sleep(wanted - sleepingHouseholds.Count);
            if (justSaved) Mod.Logger.Info($"Back asleep after the save: {sleepingHouseholds.Count} households ({sleepingCitizens} people).");
            crowds.Update(EntityManager, waiting, endFrame.CreateCommandBuffer());
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
            // Through the game's end-of-frame command buffer, as its own systems change entities: never in the middle of a frame,
            // while its jobs may still be reading them (doing it directly crashed the game, test run 7 November 2026).
            var commands = endFrame.CreateCommandBuffer();
            using (var array = new NativeArray<Entity>(batch.ToArray(), Allocator.Temp)) commands.AddComponent<Disabled>(array);
        }

        /// <summary>
        /// Wakes the households asleep longest, as many as the game time since the last top-up is due. They're still disabled
        /// until the end of the frame, so this top-up's sleeping picks others.
        /// </summary>
        private void Rotate()
        {
            uint now = simulation.frameIndex;
            uint elapsed = now - lastRotation;
            lastRotation = now;
            float rotationFrames = RotationDays * FramesPerDay;
            // Paused, or a jump (a city just loaded): nothing due.
            if (elapsed == 0 || elapsed > rotationFrames || sleepingHouseholds.Count == 0) return;
            rotationDue += sleepingHouseholds.Count * (double)elapsed / rotationFrames;
            int count = System.Math.Min((int)rotationDue, sleepingHouseholds.Count);
            if (count == 0) return;
            rotationDue -= count;
            var batch = new List<Entity>();
            for (int index = 0; index < count; index++)
            {
                var household = sleepingHouseholds[index];
                sleepingSet.Remove(household);
                if (!EntityManager.Exists(household)) continue;
                batch.Add(household);
                var members = EntityManager.GetBuffer<HouseholdCitizen>(household, true);
                sleepingCitizens -= members.Length;
                for (int at = 0; at < members.Length; at++)
                {
                    var citizen = members[at].m_Citizen;
                    batch.Add(citizen);
                    // Members who fell asleep out on foot sleep with their walker.
                    if (!EntityManager.HasComponent<CurrentTransport>(citizen)) continue;
                    var walker = EntityManager.GetComponentData<CurrentTransport>(citizen).m_CurrentTransport;
                    if (EntityManager.Exists(walker) && EntityManager.HasComponent<Disabled>(walker)) batch.Add(walker);
                }
            }
            sleepingHouseholds.RemoveRange(0, count);
            if (batch.Count == 0) return;
            using (var array = new NativeArray<Entity>(batch.ToArray(), Allocator.Temp)) endFrame.CreateCommandBuffer().RemoveComponent<Disabled>(array);
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
            crowds.TakeAll();
            sleepingHouseholds.Clear();
            sleepingSet.Clear();
            sleepingCitizens = 0;
            // Everyone with the tag, not only the ones this system remembers: the game never disables anything itself, so
            // nobody can be left asleep by a slip in the bookkeeping.
            using (var everyone = asleep.ToEntityArray(Allocator.Temp))
            {
                if (everyone.Length == 0) return;
                // At the end of the frame, like every other tag change: changing them mid-frame crashed the game (test run,
                // 7 November 2026). A save waits for it (SaveHoldPatch).
                endFrame.CreateCommandBuffer().RemoveComponent<Disabled>(everyone);
                wakeProbe = everyone[everyone.Length - 1];
                Mod.Logger.Info($"Woke {everyone.Length} people and households{(SaveInProgress ? " for a save" : "")}.");
            }
        }

        [Preserve]
        protected override void OnGameLoadingComplete(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            base.OnGameLoadingComplete(purpose, mode);
            lastLimit = -1;
            // The first top-up on the first frame: a city loads with everyone awake, and in the 120 frames of waiting they asked
            // for 15,000 routes at once (test run, 8 November 2026).
            frames = TopUpFrames;
            lastRotation = simulation.frameIndex;
            rotationDue = 0;
            ready = mode == GameMode.Game;
            // A freshly loaded city has nobody asleep (saves are written awake); this also wakes anyone a save might still
            // hold asleep.
            WakeAll();
        }

        [Preserve]
        protected override void OnGamePreload(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);
            ready = false;
        }
    }

    /// <summary>
    /// The game writes a save in SaveGameSystem's update, a frame or more after RunOnce asks for it. Until the sleepers woken for
    /// it are awake (the tag comes off at the end of a frame), the writing waits; at most 30 frames, so a save never hangs.
    /// </summary>
    [HarmonyPatch(typeof(SaveGameSystem), "OnUpdate")]
    internal static class SaveHoldPatch
    {
        private static int held;

        private static bool Prefix(SaveGameSystem __instance)
        {
            var dormancy = DormancySystem.Instance;
            if (__instance.m_Writing || dormancy == null || dormancy.Awake || ++held > 30)
            {
                if (held > 30) Mod.Logger.Warn("A save went ahead before everyone was awake.");
                held = 0;
                return true;
            }
            return false;
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
