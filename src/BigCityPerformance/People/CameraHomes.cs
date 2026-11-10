// Big City Performance. Original work by TevBenji, 2026-11. GPL-3.0.
using System.Collections.Generic;
using Game.Buildings;
using Game.Common;
using Game.Rendering;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace BigCityPerformance.People
{
    /// <summary>
    /// The homes where the camera looks. With a limit of fifty thousand spread evenly over a city of millions, any one street
    /// has almost nobody on it. So the households living around the point the camera looks at are woken first, a few each
    /// top-up, and kept awake while it stays, up to <see cref="Share"/> of everyone awake; the rest of the awake stay spread
    /// over the city and keep taking turns. Looking at the whole city from far up shows no single street, so nothing is
    /// preferred then.
    /// </summary>
    internal sealed class CameraHomes
    {
        /// <summary>The test runner turns it off to compare.</summary>
        internal static bool On = true;

        // Metres around the camera's pivot: as far as the camera is from it, within these bounds; from further up than
        // NoViewBeyond no street is in view.
        private const float NearestView = 300f, WidestView = 1000f, NoViewBeyond = 3000f;

        /// <summary>The most of the awake households that may be the ones in view.</summary>
        private const float Share = 0.6f;

        private const float RefreshSeconds = 10f;

        // With no view (the game window in the background, another camera mode) the last one is kept this long, so coming
        // back finds the same street awake.
        private const float KeepSeconds = 120f;

        // Forty households a second is about a hundred people starting their day: a street fills in a minute or two without a
        // wave of route requests.
        private const int MostWokenATopUp = 40, MostBuildingsATopUp = 300;

        private readonly HashSet<Entity> homes = new HashSet<Entity>();
        private readonly List<Entity> homeList = new List<Entity>();
        private EntityQuery residences, awakeHouseholds;
        private float3 lastPivot;
        private float lastRadius, nextRefresh, lastView;
        private int next;

        internal int Homes => homes.Count;

        /// <summary>Awake households living in view: counted at each refresh, plus the ones woken since.</summary>
        internal int Awake { get; private set; }

        /// <summary>True while households in view are left awake when others are put to sleep.</summary>
        internal bool Protecting { get; private set; }

        internal bool Contains(Entity building) => homes.Contains(building);

        internal void Create(DormancySystem system)
        {
            var entities = system.EntityManager;
            residences = entities.CreateEntityQuery(ComponentType.ReadOnly<Building>(), ComponentType.ReadOnly<ResidentialProperty>(), ComponentType.ReadOnly<Renter>(),
                ComponentType.ReadOnly<Game.Objects.Transform>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
            awakeHouseholds = entities.CreateEntityQuery(ComponentType.ReadOnly<Game.Citizens.Household>(), ComponentType.ReadOnly<PropertyRenter>(),
                ComponentType.ReadOnly<Game.Citizens.HouseholdCitizen>(), ComponentType.Exclude<Game.Citizens.TouristHousehold>(),
                ComponentType.Exclude<Game.Citizens.CommuterHousehold>(), ComponentType.Exclude<Game.Agents.MovingAway>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
        }

        internal void Reset()
        {
            homes.Clear();
            homeList.Clear();
            Awake = 0;
            Protecting = false;
            nextRefresh = 0f;
        }

        /// <summary>
        /// Keeps the list of homes in view up to date and wakes a few of their sleeping households into <paramref name="woken"/>.
        /// <paramref name="awakeBudget"/> is how many households the limit lets be awake.
        /// </summary>
        internal void Update(DormancySystem system, int awakeBudget, HashSet<Entity> sleeping, List<Entity> woken)
        {
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (!On || !View(system, out float3 pivot, out float radius))
            {
                if (homes.Count > 0 && (!On || now - lastView > KeepSeconds)) Reset();
                return;
            }
            lastView = now;
            if (now >= nextRefresh || math.distance(pivot.xz, lastPivot.xz) > radius / 3f || math.abs(radius - lastRadius) > lastRadius * 0.3f)
            {
                Refresh(pivot, radius);
                nextRefresh = now + RefreshSeconds;
            }
            int room = (int)(awakeBudget * Share) - Awake;
            Protecting = homes.Count > 0 && room >= 0;
            int most = System.Math.Min(MostWokenATopUp, room), found = 0;
            var entities = system.EntityManager;
            for (int looked = 0; looked < MostBuildingsATopUp && looked < homeList.Count && found < most; looked++)
            {
                var building = homeList[next++ % homeList.Count];
                if (!entities.Exists(building) || !entities.HasBuffer<Renter>(building)) continue;
                var renters = entities.GetBuffer<Renter>(building, true);
                for (int at = 0; at < renters.Length && found < most; at++)
                {
                    var household = renters[at].m_Renter;
                    if (!sleeping.Remove(household)) continue;
                    system.Wake(household, woken);
                    found++;
                }
            }
            Awake += found;
        }

        /// <summary>The point the player's camera looks at and how far around it counts as in view; false from far up or in another camera mode.</summary>
        private static bool View(DormancySystem system, out float3 pivot, out float radius)
        {
            pivot = default;
            radius = 0f;
            var cameras = system.World.GetExistingSystemManaged<CameraUpdateSystem>();
            var controller = cameras?.gamePlayController;
            if (controller == null || !ReferenceEquals(cameras.activeCameraController, controller)) return false;
            float zoom = controller.zoom;
            if (zoom > NoViewBeyond) return false;
            pivot = controller.pivot;
            radius = math.clamp(zoom, NearestView, WidestView);
            return true;
        }

#if BCP_DEV
        /// <summary>What the camera part sees (test runner only).</summary>
        internal string DevState(DormancySystem system)
        {
            var cameras = system.World.GetExistingSystemManaged<CameraUpdateSystem>();
            var controller = cameras?.gamePlayController;
            bool view = View(system, out float3 pivot, out float radius);
            return $"camera part on {On}, camera system {cameras != null}, play camera {controller != null}, active is play camera {controller != null && ReferenceEquals(cameras.activeCameraController, controller)}, "
                + $"zoom {(controller != null ? controller.zoom : -1f):F0}, view {view} at ({pivot.x:F0}, {pivot.z:F0}) radius {radius:F0}, residential buildings {residences.CalculateEntityCount()}, "
                + $"homes in view {homes.Count}, awake in view {Awake}";
        }
#endif

        private void Refresh(float3 pivot, float radius)
        {
            homes.Clear();
            homeList.Clear();
            lastPivot = pivot;
            lastRadius = radius;
            float reach = radius * radius;
            using (var buildings = residences.ToEntityArray(Allocator.Temp))
            using (var places = residences.ToComponentDataArray<Game.Objects.Transform>(Allocator.Temp))
                for (int index = 0; index < buildings.Length; index++)
                {
                    if (math.distancesq(places[index].m_Position.xz, pivot.xz) > reach) continue;
                    homes.Add(buildings[index]);
                    homeList.Add(buildings[index]);
                }
            int awake = 0;
            if (homes.Count > 0)
                using (var renting = awakeHouseholds.ToComponentDataArray<PropertyRenter>(Allocator.Temp))
                    for (int index = 0; index < renting.Length; index++)
                        if (homes.Contains(renting[index].m_Property)) awake++;
            Awake = awake;
        }
    }
}
