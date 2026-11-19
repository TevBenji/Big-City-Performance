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
    /// Households go to sleep once nobody is out: anyone on foot, riding or waiting for transport (a taxi hailed at the kerb
    /// included) keeps the household awake, so no car, vehicle or stop waits for someone who isn't there. Saves stay ordinary and load without the mod: sleepers are written as ordinary households (<see cref="SaveQueryPatch"/>).
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
        private bool afterSave, wokeForSave;
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

        /// <summary>Set by the save patch until the save is written: nobody goes to sleep or wakes while it is.</summary>
        internal static bool SaveInProgress;

        // The last entity woken; once it's awake, the whole batch is (one command buffer).
        private Entity wakeProbe;

        /// <summary>
        /// Set while the clock waits after a city loads: everyone loads awake, and in the first moments they asked for 15,000
        /// routes at once (test run, 8 November 2026). The clock starts once the first sleepers are asleep, or after
        /// <see cref="MostHoldFrames"/> frames whatever happens.
        /// </summary>
        internal static bool HoldClock;
        private const int MostHoldFrames = 90;
        private int heldFrames;
        private Entity sleepProbe;

        /// <summary>True once everyone woken has had the tag taken off.</summary>
        internal bool Awake => wakeProbe == Entity.Null || !EntityManager.Exists(wakeProbe) || !EntityManager.HasComponent<Disabled>(wakeProbe);
        internal static DormancySystem Instance;

        internal int SleepingHouseholds => sleepingHouseholds.Count;
        internal int SleepingCitizens => sleepingCitizens;

        /// <summary>
        /// The population's share in awake households. Counting sleepers' members against the game's population can go below
        /// zero: the game's count leaves some members out (test run, 8 November 2026); household counts are exact on both sides.
        /// </summary>
        internal int PeopleInDetail(int population)
        {
            int awake = households.CalculateEntityCount();
            int total = awake + sleepingHouseholds.Count;
            return total == 0 ? population : (int)((long)population * awake / total);
        }
        internal int SleepingAtStops => crowds.Asleep;
        /// <summary>Every household with a home, asleep or awake.</summary>
        internal int Households => households.CalculateEntityCount() + sleepingHouseholds.Count;
        internal bool Ready => ready;
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
            // Not someone who has hailed a taxi: the taxi they called waits for them in particular (see AllIndoors).
            waiting = GetEntityQuery(ComponentType.ReadOnly<Human>(), ComponentType.ReadOnly<Resident>(), ComponentType.ReadOnly<HumanCurrentLane>(),
                ComponentType.Exclude<CurrentVehicle>(), ComponentType.Exclude<RideNeeder>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
            asleep = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Disabled>() },
                Any = new[] { ComponentType.ReadOnly<Household>(), ComponentType.ReadOnly<Citizen>(), ComponentType.ReadOnly<Human>() },
                Options = EntityQueryOptions.IncludeDisabledEntities,
            });
            SleeperBookkeeping.Apply(World);
        }

        /// <summary>The most people simulated in detail; 0 = everyone (the game as it is).</summary>
        private static int Limit => Mod.Settings == null || !Mod.Settings.LimitPeople ? 0
            : Mod.Settings.AutoLimit ? ClockWatch.Auto.Limit : Mod.Settings.PeopleInDetail * 1000;

        /// <summary>The city's population as the game counts it, sleepers included (see <see cref="SleeperBookkeeping"/>).</summary>
        internal int Population()
        {
            var city = citySystem.City;
            return city != Entity.Null && EntityManager.HasComponent<Game.City.Population>(city) ? EntityManager.GetComponentData<Game.City.Population>(city).m_Population : 0;
        }

        [Preserve]
        protected override void OnUpdate()
        {
            if (SaveInProgress)
            {
                // Nothing changes until the game has written the save. If everyone was woken for it, they sleep again at once:
                // they're still at home.
                if (saveGame.Enabled) return;
                SaveInProgress = false;
                if (wokeForSave)
                {
                    wokeForSave = false;
                    frames = TopUpFrames;
                    afterSave = true;
                }
            }
            if (HoldClock) ReleaseClockOnceAsleep();
            if (!ready || ++frames < TopUpFrames) return;
            frames = 0;
            bool justSaved = afterSave;
            afterSave = false;
            int limit = Limit;
            int people = Population();
            if (limit == 0 || people <= limit)
            {
                // No limit, or a city under it: nobody sleeps, at stops included (code review, 8 November 2026: they can be
                // the only sleepers).
                if (sleepingHouseholds.Count > 0 || crowds.Asleep > 0) WakeAll();
                return;
            }
            float awake = (float)limit / people;
            int total = households.CalculateEntityCount() + sleepingHouseholds.Count;
            int wanted = (int)(total * (1f - awake));
            Rotate();
            // A higher limit (the setting, or the automatic limit finding room) wakes only the difference, longest asleep first:
            // waking everyone for every change churned the whole city (test run, 7 November 2026). A little slack, so drift in the
            // counts doesn't wake and re-sleep the same few households.
            int over = sleepingHouseholds.Count - wanted;
            if (over > System.Math.Max(10, wanted / 50)) WakeLongestAsleep(over);
            else if (wanted > sleepingHouseholds.Count) Sleep(wanted - sleepingHouseholds.Count);
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
                        if (!AllIndoors(members, walkers) || HasAdultLearner(members)) continue;
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
            sleepProbe = batch[batch.Count - 1];
        }

        /// <summary>Lets the clock go once the first sleepers after loading are asleep (their tag lands at the end of a frame).</summary>
        private void ReleaseClockOnceAsleep()
        {
            heldFrames++;
            bool asleepNow = sleepProbe != Entity.Null && EntityManager.Exists(sleepProbe) && EntityManager.HasComponent<Disabled>(sleepProbe);
            // Nobody to put to sleep (a city under the limit): no reason to wait past the first top-up.
            bool nobody = heldFrames > 1 && sleepProbe == Entity.Null;
            if (!asleepNow && !nobody && heldFrames <= MostHoldFrames) return;
            HoldClock = false;
            Mod.Logger.Info($"The clock waited {heldFrames} frames after loading, while {sleepingHouseholds.Count:N0} households went to sleep.");
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
            WakeLongestAsleep(count);
        }

        /// <summary>Wakes the <paramref name="count"/> households asleep longest, with their members and walkers.</summary>
        private void WakeLongestAsleep(int count)
        {
            count = System.Math.Min(count, sleepingHouseholds.Count);
            var batch = new List<Entity>();
            for (int index = 0; index < count; index++)
            {
                var household = sleepingHouseholds[index];
                sleepingSet.Remove(household);
                if (!EntityManager.Exists(household)) continue;
                ForgetSleepShopping(household);
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
            ShopTwin.LeftTheFront(count);
            if (batch.Count == 0) return;
            using (var array = new NativeArray<Entity>(batch.ToArray(), Allocator.Temp)) endFrame.CreateCommandBuffer().RemoveComponent<Disabled>(array);
        }

        /// <summary>Dev only: sleepers shop as before 0.3.13, in debt if need be and with their asleep shopping kept (for comparing).</summary>
        internal static bool DevOldShopping = false;

        /// <summary>Dev only: households sleep with someone waiting for a hearse or an ambulance, as before 0.3.13 (for comparing).</summary>
        internal static bool DevOldHealth = false;

        /// <summary>
        /// A household waking starts a new day of shopping. Asleep, the game's daily turnover of "shopped today" into "shopped
        /// last day" never ran, so every purchase the sleepers' shopping (<see cref="ShopTwin"/>) charged it piled up, two days'
        /// worth or more; awake, that pile became its "last day", raised the average the sleepers' shopping copies, and every
        /// sleeper was charged more the next hour. In a 300,000-person city sleepers came to spend 20 times what they did in a
        /// fresh one, ran into debt and moved out by the thousand (a player's logs and graphs, 19 November 2026). Its last real
        /// day stays as it was before it slept.
        /// </summary>
        private void ForgetSleepShopping(Entity household)
        {
            if (DevOldShopping) return;
            var data = EntityManager.GetComponentData<Household>(household);
            data.m_ShoppedValuePerDay = 0;
            data.m_LastDayFrameIndex = simulation.frameIndex;
            EntityManager.SetComponentData(household, data);
        }

        /// <summary>Dev only: members out on foot sleep where they stand, with their walker, as before 0.3.12 (for comparing).</summary>
        internal static bool DevWalkersSleep = false;

        /// <summary>True when every member is indoors (with <see cref="DevWalkersSleep"/>, out on foot too: their walkers listed).</summary>
        /// <remarks>
        /// Someone who hails a taxi at the kerb has a ride request but no waiting flag, and steps along the kerb to meet the taxi.
        /// Put to sleep then, the taxi came and waited for them until their next turn awake, blocking the road (a player's report, 21
        /// taxis in two saves, 15 November 2026). The game keeps a dispatched taxi waiting for as long as its fare holds the request.
        /// </remarks>
        private bool AllIndoors(DynamicBuffer<HouseholdCitizen> members, List<Entity> walkers)
        {
            for (int at = 0; at < members.Length; at++)
            {
                var citizen = members[at].m_Citizen;
                // Waiting for a hearse or an ambulance: the game's health system skips sleepers, so the body or the patient never
                // came out and the hearse waited at the door for good, blocking the road (a player's report, 18 November 2026).
                if (!DevOldHealth && EntityManager.HasComponent<HealthProblem>(citizen)
                    && (EntityManager.GetComponentData<HealthProblem>(citizen).m_Flags & HealthProblemFlags.RequireTransport) != 0) return false;
                if (!EntityManager.HasComponent<CurrentTransport>(citizen)) continue;
                var walker = EntityManager.GetComponentData<CurrentTransport>(citizen).m_CurrentTransport;
                if (!EntityManager.Exists(walker) || !EntityManager.HasComponent<Human>(walker) || !EntityManager.HasComponent<Resident>(walker)) return false;
                var flags = EntityManager.GetComponentData<Resident>(walker).m_Flags;
                if (EntityManager.HasComponent<CurrentVehicle>(walker) || EntityManager.HasComponent<RideNeeder>(walker)
                    || (flags & (ResidentFlags.InVehicle | ResidentFlags.WaitingTransport | ResidentFlags.Disembarking)) != 0
                    || (EntityManager.GetComponentData<Human>(walker).m_Flags & HumanFlags.Waiting) != 0) return false;
                // Asleep on foot, a walker stood frozen wherever it was, a crosswalk included, and cars waited at the junction for
                // it to cross (a player's 900,000-person city, 17 November 2026). The household sleeps once they're indoors.
                if (!DevWalkersSleep) return false;
                walkers.Add(walker);
            }
            return true;
        }

        /// <summary>
        /// True if a member is an adult at elementary school, which the game never does by itself: Adult Basic Education's learners.
        /// That mod follows its learners through a query that can't see sleepers, so one asleep looked to it like one who had
        /// left school (a player's 3-million city, 15 November 2026).
        /// </summary>
        private bool HasAdultLearner(DynamicBuffer<HouseholdCitizen> members)
        {
            for (int at = 0; at < members.Length; at++)
            {
                var citizen = members[at].m_Citizen;
                if (!EntityManager.HasComponent<Game.Citizens.Student>(citizen) || EntityManager.GetComponentData<Game.Citizens.Student>(citizen).m_Level != 1) continue;
                var age = EntityManager.GetComponentData<Citizen>(citizen).GetAge();
                if (age == CitizenAge.Adult || age == CitizenAge.Elderly) return true;
            }
            return false;
        }

        /// <summary>Everyone awake: when the setting goes back up, when the mod unloads, or before a save if its patch failed.</summary>
        internal void WakeAll()
        {
            Forget();
            // Everyone with the tag, not only the ones this system remembers: the game never disables anything itself, so
            // nobody can be left asleep by a slip in the bookkeeping.
            using (var everyone = asleep.ToEntityArray(Allocator.Temp))
            {
                if (everyone.Length == 0) return;
                foreach (var entity in everyone) if (EntityManager.HasComponent<Household>(entity)) ForgetSleepShopping(entity);
                // At the end of the frame, like every other tag change: changing them mid-frame crashed the game (test run,
                // 7 November 2026). A save waits for it (SaveHoldPatch).
                endFrame.CreateCommandBuffer().RemoveComponent<Disabled>(everyone);
                wakeProbe = everyone[everyone.Length - 1];
                wokeForSave = SaveInProgress;
                Mod.Logger.Info($"Woke {everyone.Length} people and households{(SaveInProgress ? " for a save" : "")}.");
            }
        }

        private void Forget()
        {
            crowds.TakeAll();
            sleepingHouseholds.Clear();
            sleepingSet.Clear();
            sleepingCitizens = 0;
        }

        /// <summary>
        /// A city loaded. It has nobody asleep: the Disabled tag isn't saved. Anyone still tagged was left from the city before
        /// (the game's clear takes sleepers with it, see <see cref="SleeperBookkeeping"/>): a ghost of a city that's gone, so it's
        /// removed, never woken. Waking them doubled a player's households (12 November 2026).
        /// </summary>
        private void RemoveLeftovers()
        {
            Forget();
            using (var leftovers = asleep.ToEntityArray(Allocator.Temp))
            {
                if (leftovers.Length == 0) return;
                endFrame.CreateCommandBuffer().DestroyEntity(leftovers);
                Mod.Logger.Warn($"{leftovers.Length} people and households were still asleep from the city before: removed, not woken.");
            }
        }

        [Preserve]
        protected override void OnGameLoadingComplete(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            base.OnGameLoadingComplete(purpose, mode);
            // The first top-up on the first frame: a city loads with everyone awake, and in the 120 frames of waiting they asked
            // for 15,000 routes at once (test run, 8 November 2026).
            frames = TopUpFrames;
            lastRotation = simulation.frameIndex;
            rotationDue = 0;
            ready = mode == GameMode.Game;
            HoldClock = ready && Limit > 0;
            heldFrames = 0;
            sleepProbe = Entity.Null;
            CountPace.Reset();
            // Reflection on another mod's system: a failure is logged, never in the way of loading the city.
            try { SleeperBookkeeping.ApplyToOtherMods(World); }
            catch (System.Exception exception) { Mod.Logger.Error(exception, "Big City Performance: other mods' household queries failed"); }
            RemoveLeftovers();
        }

        [Preserve]
        protected override void OnGamePreload(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);
            ready = false;
        }
    }

    /// <summary>
    /// The game writes a save in SaveGameSystem's update, a frame or more after RunOnce asks for it. If sleepers had to be woken
    /// for it, the writing waits until they are awake (the tag comes off at the end of a frame); at most 30 frames, so a save
    /// never hangs.
    /// </summary>
    [HarmonyPatch(typeof(SaveGameSystem), "OnUpdate")]
    internal static class SaveHoldPatch
    {
        private static int held;

        private static bool Prefix(SaveGameSystem __instance)
        {
            var dormancy = DormancySystem.Instance;
            if (SaveQueryPatch.Applied || __instance.m_Writing || dormancy == null || dormancy.Awake || ++held > 30)
            {
                if (held > 30) Mod.Logger.Warn("A save went ahead before everyone was awake.");
                held = 0;
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// While <see cref="DormancySystem.HoldClock"/> is set after a city loads, the simulation doesn't step (drawing goes on): a
    /// moment at the very start of play, while the first sleepers go to sleep.
    /// </summary>
    [HarmonyPatch(typeof(SimulationSystem), "OnUpdate")]
    internal static class LoadHoldPatch
    {
        private static bool Prefix(SimulationSystem __instance) => !DormancySystem.HoldClock || __instance.m_IsLoading;
    }

    /// <summary>Marks a save as started; wakes everyone first only if the save writer couldn't be made to include sleepers.</summary>
    [HarmonyPatch(typeof(SaveGameSystem), nameof(SaveGameSystem.RunOnce))]
    internal static class SaveWakePatch
    {
        private static void Prefix()
        {
            DormancySystem.SaveInProgress = true;
            if (!SaveQueryPatch.Applied) DormancySystem.Instance?.WakeAll();
        }
    }
}
