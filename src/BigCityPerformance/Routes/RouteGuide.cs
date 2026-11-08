// Big City Performance. Original work by TevBenji, 2026-11. GPL-3.0.
using Game.Pathfind;
using HarmonyLib;

namespace BigCityPerformance.Routes
{
    /// <summary>
    /// Weighted A*: the route-finder is an A* search guided by a cost-per-metre estimate for each way of travelling
    /// (PathfindHeuristicData, from NetInitializeSystem.GetHeuristicData, handed to every search). It must assume the cheapest
    /// way, so in a city with transit the estimate is weak and each search fans out over 5,000 to 36,000 points (test runs,
    /// 7 November 2026). Scaling the estimate by <see cref="Weight"/> makes each search head for its goal: far fewer points,
    /// for routes at most <see cref="Weight"/> times the best cost (in practice close to the best). Failed searches stop sooner
    /// too, since the search gives up once its estimate passes the trip's cost limit.
    /// </summary>
    [HarmonyPatch(typeof(Game.Prefabs.NetInitializeSystem), nameof(Game.Prefabs.NetInitializeSystem.GetHeuristicData))]
    internal static class RouteGuide
    {
        /// <summary>1 = the game's own search.</summary>
        internal static float Weight = 1f;

        private static void Postfix(ref PathfindHeuristicData __result)
        {
            float weight = Weight;
            if (weight == 1f) return;
            __result.m_CarCosts.m_Value *= weight;
            __result.m_TrackCosts.m_Value *= weight;
            __result.m_PedestrianCosts.m_Value *= weight;
            __result.m_FlyingCosts.m_Value *= weight;
            __result.m_OffRoadCosts.m_Value *= weight;
            __result.m_TaxiCosts.m_Value *= weight;
        }
    }
}
