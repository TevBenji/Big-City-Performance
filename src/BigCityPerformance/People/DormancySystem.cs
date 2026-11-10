// Big City Performance. Original work by TevBenji, 2026-11. GPL-3.0.
using System.Collections.Generic;
using BigCityPerformance.Core;
using Game;
using Game.Buildings;
using Game.Citizens;
using Game.Common;
using Game.Creatures;
using Game.Pathfind;
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
    /// Households with everyone indoors or on foot go to sleep first; members out on foot sleep where they stand, with their
    /// walker. When those aren't enough to reach the limit, households with someone riding or waiting for transport sleep too,
    /// and those travellers are sent home first (<see cref="SendHome"/>). In a three-million city on a slow computer the clock
    /// ran too slowly for travellers ever to get home, and four times the limit stayed awake (test runs, 9 November 2026).
    /// Saves stay ordinary and load without the mod: sleepers are written as ordinary households (<see cref="SaveQueryPatch"/>).
    ///
    /// The game's own counts, wages and residential tax still cover sleepers (<see cref="SleeperBookkeeping"/>).
    ///
    /// Sleepers take turns: the ones asleep longest wake a few at a time, and the next top-up puts as many others to sleep, so
    /// every household gets its time awake (growing older, changing jobs, moving) about once every <see cref="RotationDays"/>
    /// game days, while the number awake stays at the limit. Households living where the camera looks are woken first and
    /// kept awake (<see cref="CameraHomes"/>), so the streets on screen are the busy ones.
    /// </summary>
    public sealed partial class DormancySystem : GameSystemBase
    {
        /// <summary>
        /// Seconds between top-ups: each is one batch of tag changes (a sync point), so not every frame. By the clock on the
        /// wall: counted in rendered frames, a top-up came once a minute at the two frames a second of a three-million city.
        /// </summary>
        private const float TopUpSeconds = 1f;

        /// <summary>
        /// Households looked at in one top-up. Looking at every awake household took a quarter of a second in a three-million
        /// city (test run, 9 November 2026); the next top-up carries on with others. Far from the limit (the option just
        /// turned on in a big city) an eighth of what's missing is looked at instead, so it's there in seconds, not minutes.
        /// </summary>
        private const int MostExamined = 6000;

        /// <summary>
        /// Households whose travellers are sent home in one top-up: this many, or a quarter of what's missing up to
        /// <see cref="MostSentHomeAtOnce"/> (14,000 in one frame made no slow frame; test run, 9 November 2026).
        /// </summary>
        internal static int MostSentHome = 1000;
        private const int MostSentHomeAtOnce = 20000;

        /// <summary>Game days for every sleeper to have had a turn awake.</summary>
        internal static float RotationDays = 2f;

        /// <summary>
        /// The most households that take their turn in a game day. Two days for everyone woke 126 households a second in a
        /// three-million city, each starting its day with new trips; this is the rate of a city of about a million, and a
        /// bigger city's households wait longer for their turn instead.
        /// </summary>
        internal static int MostTurnsADay = 200000;

        /// <summary>Whether travellers may be sent home to let their household sleep. The test runner turns it off to compare.</summary>
        internal static bool SendTravellersHome = true;

        // A game day is 262,144 simulation frames.
        private const float FramesPerDay = 262144f;

        // Who is asleep, each with the number of its current sleep, and a ticket for every sleep in the order they began
        // (longest asleep first). A household woken out of turn leaves its ticket behind: a ticket only counts while its
        // number is the household's current one, so an old one is passed over, by the turns and by the shopping alike, even
        // if the household has gone back to sleep since.
        private readonly Dictionary<Entity, int> sleeping = new Dictionary<Entity, int>();
        private readonly List<Ticket> tickets = new List<Ticket>();
        private int lastTicket, nextBuyer;
        private readonly List<Entity> batch = new List<Entity>(), walkers = new List<Entity>();
        private readonly List<Traveller> travellers = new List<Traveller>();
        private readonly List<int> withTravellers = new List<int>();
        private int sleepingCitizens, cursor, shortTopUps;
        private float nextTopUp;
        private bool afterSave, wokeForSave;
        private EntityQuery households, waiting, asleep;

        // Only in a fully loaded city in play: sleeping people while a city loads lost track of them when loading finished
        // (test run, 7 November 2026).
        private bool ready;
        private readonly StopCrowds crowds = new StopCrowds();
        private readonly CameraHomes camera = new CameraHomes();
        private SaveGameSystem saveGame;
        private CitySystem citySystem;
        private EndFrameBarrier endFrame;
        private SimulationSystem simulation;
        private uint lastRotation;
        private double rotationDue;

        private struct Traveller
        {
            public Entity Citizen, Walker;
        }

        private struct Ticket
        {
            public Entity Household;
            public int Number;
        }

        private bool Current(Ticket ticket) => sleeping.TryGetValue(ticket.Household, out int number) && number == ticket.Number;

        /// <summary>Set by the save patch until the save is written: nobody goes to sleep or wakes while it is.</summary>
        internal static bool SaveInProgress;

        // The last entity woken; once it's awake, the whole batch is (one command buffer).
        private Entity wakeProbe;

        /// <summary>
        /// Set while the clock waits after a city loads: everyone loads awake, and in the first moments they asked for 15,000
        /// routes at once (test run, 8 November 2026). The clock starts once everyone who can sleep where they are is asleep,
        /// or after <see cref="MostHoldSeconds"/> whatever happens.
        /// </summary>
        internal static bool HoldClock;
        private const float MostHoldSeconds = 30f;
        private const int HoldTopUpFrames = 5;
        private int heldFrames, lastSlept;
        private float holdStart;
        private Entity sleepProbe;

        /// <summary>True once everyone woken has had the tag taken off.</summary>
        internal bool Awake => wakeProbe == Entity.Null || !EntityManager.Exists(wakeProbe) || !EntityManager.HasComponent<Disabled>(wakeProbe);
        internal static DormancySystem Instance;

        internal int SleepingHouseholds => sleeping.Count;
        internal int SleepingCitizens => sleepingCitizens;

        /// <summary>People sent home since the city loaded, so their household could sleep.</summary>
        internal int SentHome { get; private set; }

        /// <summary>
        /// The population's share in awake households. Counting sleepers' members against the game's population can go below
        /// zero: the game's count leaves some members out (test run, 8 November 2026); household counts are exact on both sides.
        /// </summary>
        internal int PeopleInDetail(int population)
        {
            int awake = households.CalculateEntityCount();
            int total = awake + sleeping.Count;
            return total == 0 ? population : (int)((long)population * awake / total);
        }
        internal int SleepingAtStops => crowds.Asleep;
        internal bool Ready => ready;

        /// <summary>
        /// The next sleeping household in turn, for the shop twin's sales: each call moves one on, so every sleeper pays its
        /// share over time. Null with nobody asleep.
        /// </summary>
        internal Entity NextSleeper()
        {
            for (int tried = 0; tried < tickets.Count; tried++)
            {
                nextBuyer = (nextBuyer + 1) % tickets.Count;
                if (Current(tickets[nextBuyer])) return tickets[nextBuyer].Household;
            }
            return Entity.Null;
        }

        /// <summary>Wakes one sleeping household whatever its turn (it lives where the camera looks); false if it isn't asleep.</summary>
        internal bool WakeOutOfTurn(Entity household, List<Entity> woken)
        {
            if (!sleeping.Remove(household)) return false;
            Wake(household, woken);
            return true;
        }

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            Instance = this;
            saveGame = World.GetOrCreateSystemManaged<SaveGameSystem>();
            endFrame = World.GetOrCreateSystemManaged<EndFrameBarrier>();
            citySystem = World.GetOrCreateSystemManaged<CitySystem>();
            simulation = World.GetOrCreateSystemManaged<SimulationSystem>();
            // Households that live here and are staying: one moving away is on its way out of the city and has to get there.
            households = GetEntityQuery(ComponentType.ReadOnly<Household>(), ComponentType.ReadOnly<PropertyRenter>(), ComponentType.ReadOnly<HouseholdCitizen>(),
                ComponentType.Exclude<TouristHousehold>(), ComponentType.Exclude<CommuterHousehold>(), ComponentType.Exclude<Game.Agents.MovingAway>(),
                ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
            waiting = GetEntityQuery(ComponentType.ReadOnly<Human>(), ComponentType.ReadOnly<Resident>(), ComponentType.ReadOnly<HumanCurrentLane>(),
                ComponentType.Exclude<CurrentVehicle>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
            asleep = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Disabled>() },
                Any = new[] { ComponentType.ReadOnly<Household>(), ComponentType.ReadOnly<Citizen>(), ComponentType.ReadOnly<Human>() },
                Options = EntityQueryOptions.IncludeDisabledEntities,
            });
            camera.Create(this);
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
                    nextTopUp = 0f;
                    afterSave = true;
                }
            }
            if (!ready) return;
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (HoldClock)
            {
                // While the clock waits, everyone who can sleep where they are goes to sleep in a few quick top-ups.
                if (heldFrames++ % HoldTopUpFrames != 0) return;
                if (heldFrames > 1 && ReleaseClock(now)) nextTopUp = now + TopUpSeconds;
            }
            else if (now < nextTopUp) return;
            else nextTopUp = now + TopUpSeconds;

            bool justSaved = afterSave;
            afterSave = false;
            int limit = Limit;
            int people = Population();
            if (limit == 0 || people <= limit)
            {
                // No limit, or a city under it: nobody sleeps, at stops included (code review, 8 November 2026: they can be
                // the only sleepers).
                if (sleeping.Count > 0 || crowds.Asleep > 0) WakeAll();
                lastSlept = 0;
                return;
            }
            float awake = (float)limit / people;
            int total = households.CalculateEntityCount() + sleeping.Count;
            int wanted = (int)(total * (1f - awake));
            if (!HoldClock)
            {
                Rotate();
                camera.Update(this, total - wanted, batch);
                WakeBatch();
            }
            // A higher limit (the setting, or the automatic limit finding room) wakes only the difference, longest asleep first:
            // waking everyone for every change churned the whole city (test run, 7 November 2026). A little slack, so drift in the
            // counts doesn't wake and re-sleep the same few households.
            int over = sleeping.Count - wanted;
            lastSlept = 0;
            if (over > System.Math.Max(10, wanted / 50)) WakeLongestAsleep(over);
            else if (wanted > sleeping.Count) lastSlept = Sleep(wanted - sleeping.Count);
            if (justSaved) Mod.Logger.Info($"Back asleep after the save: {sleeping.Count} households ({sleepingCitizens} people).");
            crowds.Update(EntityManager, waiting, endFrame.CreateCommandBuffer());
        }

        /// <summary>
        /// Puts up to <paramref name="count"/> households to sleep and returns how many it found: first those with everyone
        /// indoors or on foot, then, when two top-ups in a row came up short, those with travellers, who are sent home.
        /// </summary>
        private int Sleep(int count)
        {
            int found = 0;
            batch.Clear();
            withTravellers.Clear();
            var commands = endFrame.CreateCommandBuffer();
            using (var awake = households.ToEntityArray(Allocator.Temp))
            using (var homes = households.ToComponentDataArray<PropertyRenter>(Allocator.Temp))
            {
                int length = awake.Length;
                if (length == 0) return 0;
                // While the clock waits after loading, nothing on screen moves that could stutter: everyone is looked at.
                int budget = HoldClock ? length : System.Math.Max(MostExamined, count / 8);
                // Never while the clock waits after loading: the wait skips the simulation's whole update, with the phases the
                // game otherwise runs every frame (even paused), and removing thousands of walkers then crashed the game (test
                // run, 9 November 2026). In play, paused or not, 110,000 were sent home without a fault.
                int mostSent = SendTravellersHome && !HoldClock && shortTopUps >= 2
                    ? System.Math.Min(MostSentHomeAtOnce, System.Math.Max(MostSentHome, count / 4)) : 0;
                bool spare = camera.Protecting;
                // An even walk over the awake households, so the ones still awake are spread over the whole city. It reaches
                // every household once before any twice, and the next top-up carries on where this one stops: starting over
                // each time would look at the same households again and never reach the rest (code review, 10 November 2026).
                int step = EvenWalk.Step(length, count);
                int index = cursor % length;
                for (int looked = 0; looked < length && found < count && budget > 0; looked++, index = (index + step) % length)
                {
                    var household = awake[index];
                    if (sleeping.ContainsKey(household) || (spare && camera.Contains(homes[index].m_Property))) continue;
                    budget--;
                    var members = EntityManager.GetBuffer<HouseholdCitizen>(household, true);
                    if (!Look(members)) continue;
                    if (travellers.Count > 0)
                    {
                        if (withTravellers.Count < mostSent) withTravellers.Add(index);
                        continue;
                    }
                    PutToSleep(household, members);
                    found++;
                }
                cursor = index;
                foreach (int at in withTravellers)
                {
                    if (found >= count) break;
                    var household = awake[at];
                    var home = homes[at].m_Property;
                    var members = EntityManager.GetBuffer<HouseholdCitizen>(household, true);
                    if (!EntityManager.Exists(home) || !Look(members)) continue;
                    foreach (var traveller in travellers) SendHome(household, traveller, home, commands);
                    SentHome += travellers.Count;
                    PutToSleep(household, members);
                    found++;
                }
            }
            shortTopUps = found < count ? shortTopUps + 1 : 0;
            if (batch.Count == 0) return found;
            // Through the game's end-of-frame command buffer, as its own systems change entities: never in the middle of a frame,
            // while its jobs may still be reading them (doing it directly crashed the game, test run 7 November 2026).
            using (var array = new NativeArray<Entity>(batch.ToArray(), Allocator.Temp)) commands.AddComponent<Disabled>(array);
            sleepProbe = batch[batch.Count - 1];
            batch.Clear();
            return found;
        }

        /// <summary>
        /// Sorts a household's members who are out into <see cref="walkers"/> (on foot: they can sleep where they stand) and
        /// <see cref="travellers"/> (riding, or waiting for a ride). False when the household has to stay awake: someone ill,
        /// hurt, dead or in custody is on a journey the game runs for them (an ambulance, a hearse, a police car).
        /// </summary>
        private bool Look(DynamicBuffer<HouseholdCitizen> members)
        {
            walkers.Clear();
            travellers.Clear();
            for (int at = 0; at < members.Length; at++)
            {
                var citizen = members[at].m_Citizen;
                // Anything the game is removing this frame is gone before these changes land (they're applied at the start of
                // the next frame, after this frame's clean-up): the household waits for the next top-up.
                if (EntityManager.HasComponent<Deleted>(citizen)) return false;
                if (!EntityManager.HasComponent<CurrentTransport>(citizen)) continue;
                var walker = EntityManager.GetComponentData<CurrentTransport>(citizen).m_CurrentTransport;
                if (!EntityManager.Exists(walker) || EntityManager.HasComponent<Deleted>(walker) || !EntityManager.HasComponent<Human>(walker) || !EntityManager.HasComponent<Resident>(walker)) return false;
                var flags = EntityManager.GetComponentData<Resident>(walker).m_Flags;
                bool riding = EntityManager.HasComponent<CurrentVehicle>(walker) || (flags & (ResidentFlags.InVehicle | ResidentFlags.Disembarking)) != 0;
                bool waits = (flags & ResidentFlags.WaitingTransport) != 0 || (EntityManager.GetComponentData<Human>(walker).m_Flags & HumanFlags.Waiting) != 0;
                if (!riding && !waits)
                {
                    walkers.Add(walker);
                    continue;
                }
                if (EntityManager.HasComponent<HealthProblem>(citizen) || EntityManager.HasComponent<Criminal>(citizen)) return false;
                travellers.Add(new Traveller { Citizen = citizen, Walker = walker });
            }
            return true;
        }

        private void PutToSleep(Entity household, DynamicBuffer<HouseholdCitizen> members)
        {
            batch.Add(household);
            for (int at = 0; at < members.Length; at++) batch.Add(members[at].m_Citizen);
            batch.AddRange(walkers);
            sleepingCitizens += members.Length;
            tickets.Add(new Ticket { Household = household, Number = ++lastTicket });
            sleeping[household] = lastTicket;
#if BCP_DEV
            devEverSlept.Add(household);
#endif
        }

        /// <summary>
        /// Ends a traveller's trip on paper, the way the game itself rescues a citizen it has lost track of
        /// (CitizenTravelPurposeSystem puts one with no walker and no building back in the home building, with no errand):
        /// - the walker is removed as the game removes any walker (the Deleted tag), and the game's own clean-up takes it out
        ///   of its vehicle and off its lane, along with any pet walking with it;
        /// - the household's own car, if this was its driver or passenger, gets a failed route: the game then parks a car with
        ///   nobody in it at its keeper's home (PersonalCarAISystem). Buses, trains and taxis just lose a passenger.
        /// </summary>
        private void SendHome(Entity household, Traveller traveller, Entity home, EntityCommandBuffer commands)
        {
            var walker = traveller.Walker;
            // Asleep at a crowded stop: the game's clean-up skips anything disabled, so it wakes to be removed.
            if (EntityManager.HasComponent<Disabled>(walker)) commands.RemoveComponent<Disabled>(walker);
            commands.AddComponent<Deleted>(walker);
            commands.AddComponent(traveller.Citizen, new CurrentBuilding(home));
            commands.RemoveComponent<TravelPurpose>(traveller.Citizen);
            if (EntityManager.HasBuffer<GroupCreature>(walker))
            {
                var group = EntityManager.GetBuffer<GroupCreature>(walker, true);
                for (int at = 0; at < group.Length; at++)
                {
                    var follower = group[at].m_Creature;
                    if (!EntityManager.Exists(follower) || EntityManager.HasComponent<Deleted>(follower) || !EntityManager.HasComponent<Pet>(follower)) continue;
                    var pet = EntityManager.GetComponentData<Pet>(follower).m_HouseholdPet;
                    commands.AddComponent<Deleted>(follower);
                    if (EntityManager.Exists(pet) && !EntityManager.HasComponent<Deleted>(pet)) commands.AddComponent(pet, new CurrentBuilding(home));
                }
            }
            if (!EntityManager.HasComponent<CurrentVehicle>(walker)) return;
            var vehicle = EntityManager.GetComponentData<CurrentVehicle>(walker).m_Vehicle;
            if (EntityManager.HasComponent<Game.Vehicles.Controller>(vehicle))
            {
                var controller = EntityManager.GetComponentData<Game.Vehicles.Controller>(vehicle).m_Controller;
                if (controller != Entity.Null) vehicle = controller;
            }
            if (!EntityManager.Exists(vehicle) || !EntityManager.HasComponent<Game.Vehicles.PersonalCar>(vehicle) || !EntityManager.HasComponent<PathOwner>(vehicle)) return;
            var keeper = EntityManager.GetComponentData<Game.Vehicles.PersonalCar>(vehicle).m_Keeper;
            if (!EntityManager.HasComponent<HouseholdMember>(keeper) || EntityManager.GetComponentData<HouseholdMember>(keeper).m_Household != household) return;
            var route = EntityManager.GetComponentData<PathOwner>(vehicle);
            if ((route.m_State & PathFlags.Failed) != 0) return;
            route.m_State |= PathFlags.Failed;
            // A value, not a tag: safe to set mid-frame (the entity system finishes any job writing it first).
            EntityManager.SetComponentData(vehicle, route);
        }

        /// <summary>
        /// True once the clock may start after loading: the last top-up found nobody more to put to sleep and the sleepers'
        /// tags have landed (at the start of the next frame), or <see cref="MostHoldSeconds"/> have passed.
        /// </summary>
        private bool ReleaseClock(float now)
        {
            bool asleepNow = sleepProbe == Entity.Null || !EntityManager.Exists(sleepProbe) || EntityManager.HasComponent<Disabled>(sleepProbe);
            if ((lastSlept > 0 || !asleepNow) && now - holdStart < MostHoldSeconds) return false;
            HoldClock = false;
            Mod.Logger.Info($"The clock waited {now - holdStart:F1} s after loading, while {sleeping.Count:N0} households went to sleep.");
            return true;
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
            if (elapsed == 0 || elapsed > rotationFrames || sleeping.Count == 0) return;
            rotationDue += System.Math.Min(sleeping.Count / (double)rotationFrames, MostTurnsADay / (double)FramesPerDay) * elapsed;
            int count = System.Math.Min((int)rotationDue, sleeping.Count);
            if (count == 0) return;
            rotationDue -= count;
            WakeLongestAsleep(count);
        }

        /// <summary>Wakes the <paramref name="count"/> households asleep longest, with their members and walkers.</summary>
        private void WakeLongestAsleep(int count)
        {
            int taken = 0, index = 0;
            for (; index < tickets.Count && taken < count; index++)
            {
                // A ticket from a sleep that ended out of turn (the household lives where the camera looked): passed over.
                if (!Current(tickets[index])) continue;
                sleeping.Remove(tickets[index].Household);
                taken++;
                Wake(tickets[index].Household, batch);
            }
            tickets.RemoveRange(0, index);
            // The shopping's place in the line moves with it.
            nextBuyer = System.Math.Max(0, nextBuyer - index);
            WakeBatch();
        }

        /// <summary>Lists a sleeping household, its members and their walkers in <paramref name="woken"/> to have the tag taken off.</summary>
        private void Wake(Entity household, List<Entity> woken)
        {
            if (!EntityManager.Exists(household)) return;
            woken.Add(household);
            var members = EntityManager.GetBuffer<HouseholdCitizen>(household, true);
            sleepingCitizens -= members.Length;
            for (int at = 0; at < members.Length; at++)
            {
                var citizen = members[at].m_Citizen;
                woken.Add(citizen);
                // Members who fell asleep out on foot sleep with their walker.
                if (!EntityManager.HasComponent<CurrentTransport>(citizen)) continue;
                var walker = EntityManager.GetComponentData<CurrentTransport>(citizen).m_CurrentTransport;
                if (EntityManager.Exists(walker) && EntityManager.HasComponent<Disabled>(walker)) woken.Add(walker);
            }
        }

        private void WakeBatch()
        {
            if (batch.Count == 0) return;
            using (var array = new NativeArray<Entity>(batch.ToArray(), Allocator.Temp)) endFrame.CreateCommandBuffer().RemoveComponent<Disabled>(array);
            batch.Clear();
        }

        /// <summary>Everyone awake: when the setting goes back up, when the mod unloads, or before a save if its patch failed.</summary>
        internal void WakeAll()
        {
            crowds.TakeAll();
            tickets.Clear();
            sleeping.Clear();
            nextBuyer = 0;
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
                wokeForSave = SaveInProgress;
                Mod.Logger.Info($"Woke {everyone.Length} people and households{(SaveInProgress ? " for a save" : "")}.");
            }
        }

        [Preserve]
        protected override void OnGameLoadingComplete(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            base.OnGameLoadingComplete(purpose, mode);
            // The first top-up on the first frame: a city loads with everyone awake, and in the 120 frames of waiting they asked
            // for 15,000 routes at once (test run, 8 November 2026).
            nextTopUp = 0f;
            lastRotation = simulation.frameIndex;
            rotationDue = 0;
            ready = mode == GameMode.Game;
            HoldClock = ready && Limit > 0;
            heldFrames = 0;
            holdStart = UnityEngine.Time.realtimeSinceStartup;
            shortTopUps = lastSlept = SentHome = 0;
            sleepProbe = Entity.Null;
            camera.Reset();
#if BCP_DEV
            devEverSlept.Clear();
#endif
            // A freshly loaded city has nobody asleep (saves are written awake); this also wakes anyone a save might still
            // hold asleep.
            WakeAll();
        }

#if BCP_DEV
        // Every household that has slept since the city loaded (test runner only): were today's homeless asleep before?
        private readonly HashSet<Entity> devEverSlept = new HashSet<Entity>();

        /// <summary>Every <paramref name="every"/>th sleeping household (test runner only).</summary>
        internal IEnumerable<Entity> DevSleepers(int every)
        {
            for (int index = 0; index < tickets.Count; index += every)
                if (Current(tickets[index])) yield return tickets[index].Household;
        }

        /// <summary>Who is awake and why (test runner only): it looks at every awake household.</summary>
        internal string DevCensus()
        {
            int total, couldSleep = 0, travelling = 0, mustStay = 0, inView = 0;
            using (var awake = households.ToEntityArray(Allocator.Temp))
            using (var homes = households.ToComponentDataArray<PropertyRenter>(Allocator.Temp))
            {
                total = awake.Length;
                for (int index = 0; index < awake.Length; index++)
                {
                    if (camera.Contains(homes[index].m_Property)) inView++;
                    if (!Look(EntityManager.GetBuffer<HouseholdCitizen>(awake[index], true))) mustStay++;
                    else if (travellers.Count > 0) travelling++;
                    else couldSleep++;
                }
            }
            int homelessSleptBefore = 0;
            using (var query = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<Household>(), ComponentType.ReadOnly<HomelessHousehold>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>()))
            using (var homeless = query.ToEntityArray(Allocator.Temp))
                foreach (var household in homeless)
                    if (devEverSlept.Contains(household)) homelessSleptBefore++;
            int Count(params ComponentType[] all)
            {
                using (var query = EntityManager.CreateEntityQuery(new EntityQueryDesc { All = all, None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() } }))
                    return query.CalculateEntityCount();
            }
            return $"awake households that live here {total:N0}: {couldSleep:N0} could sleep now, {travelling:N0} have someone riding or waiting, {mustStay:N0} must stay awake, "
                + $"{inView:N0} live in view ({camera.Homes:N0} homes in view, protecting {camera.Protecting}); "
                + $"moving away {Count(ComponentType.ReadOnly<Household>(), ComponentType.ReadOnly<Game.Agents.MovingAway>()):N0} "
                + $"({Count(ComponentType.ReadOnly<Household>(), ComponentType.ReadOnly<Game.Agents.MovingAway>(), ComponentType.ReadOnly<PropertyRenter>()):N0} still with a home), "
                + $"homeless {Count(ComponentType.ReadOnly<Household>(), ComponentType.ReadOnly<HomelessHousehold>()):N0} ({homelessSleptBefore:N0} of them slept earlier in this session); "
                + $"asleep {sleeping.Count:N0} households ({tickets.Count:N0} tickets); travellers sent home since loading {SentHome:N0}; {camera.DevState(this)}";
        }
#endif

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
