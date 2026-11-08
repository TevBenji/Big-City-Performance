// Big City Performance. Original work by TevBenji, 2026-11. GPL-3.0.
using System.Collections.Generic;
using Game.Buildings;
using Game.Citizens;
using Game.Common;
using Game.Companies;
using Game.Economy;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using BigCityPerformance.Core;
using HarmonyLib;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace BigCityPerformance.People
{
    /// <summary>
    /// Sleeping households still shop, so shops keep their customers. Each game hour they spend what the awake households
    /// spent per household on their last full day (the game keeps that figure for each one), and the money goes to the shops in
    /// proportion to their real customers this month: shops awake people like get the sleepers' custom too. It's paid in many
    /// smaller sales, each by the next sleeping household in turn, so every household pays its share over time. Each purchase goes
    /// into the game's own sales queue as a sale with no trip, so the game itself moves the money and the goods, counts the
    /// customer and uses up the shop's service, as for any other sale. Never vehicles: a household buying one gets a new car.
    /// </summary>
    [HarmonyPatch(typeof(ResourceBuyerSystem), "OnUpdate")]
    internal static class ShopTwin
    {
        // A game day is 262,144 simulation frames.
        private const uint FramesPerHour = 262144u / 24u;

        private static readonly System.Reflection.MethodInfo DependencyGetter = AccessTools.PropertyGetter(typeof(SystemBase), "Dependency");

        /// <summary>At most this many sales an hour, and none smaller than <see cref="SmallestSale"/> money.</summary>
        private const int MaxSalesPerHour = 4096;
        private const long SmallestSale = 200;

        private static uint lastHour = uint.MaxValue;
        private static int nextBuyer;
        private static EntityQuery awakeHouseholds, shops;
        private static bool queriesMade;

        /// <summary>Last hour's sales and money, for the test runner.</summary>
        internal static int LastSales;
        internal static long LastSpent;

        private struct Shop
        {
            public Entity Entity;
            public Resource Resource;
            public int Weight, Stock;
        }

        private static void Prefix(ResourceBuyerSystem __instance)
        {
            var dormancy = DormancySystem.Instance;
            if (dormancy == null || dormancy.SleepingHouseholds == 0)
            {
                lastHour = uint.MaxValue;
                return;
            }
            uint hour = __instance.m_SimulationSystem.frameIndex / FramesPerHour;
            if (hour == lastHour) return;
            // The first hour only starts the count, so a whole hour is billed each time.
            bool first = lastHour == uint.MaxValue;
            lastHour = hour;
            if (first) return;
            var entities = __instance.EntityManager;
            MakeQueries(entities);
            long budget = HourlySpending(dormancy.SleepingHouseholds);
            var found = Shops(entities, out long totalWeight);
            if (budget <= 0 || totalWeight == 0) return;
            // The queue was last used by the system's own jobs, finished once its dependencies are.
            ((JobHandle)DependencyGetter.Invoke(__instance, null)).Complete();
            var prefabs = __instance.m_ResourceSystem.GetPrefabs();
            var sleepers = dormancy.SleepingHouseholdList;
            // Many smaller sales, each charged to the next sleeping household in turn (the turn carries over from hour to hour), so
            // every household pays its share over time. One sale per shop put each shop's whole share on one household, the same
            // few households every hour (code review, 8 November 2026).
            int saleCount = (int)System.Math.Max(1L, System.Math.Min(System.Math.Min(sleepers.Count, MaxSalesPerHour), budget / SmallestSale));
            var weights = new long[found.Count];
            for (int index = 0; index < found.Count; index++) weights[index] = found[index].Weight;
            var counts = ShareOut.Counts(weights, saleCount);
            long perSale = budget / saleCount;
            int sales = 0;
            long spent = 0;
            for (int index = 0; index < found.Count; index++)
            {
                if (counts[index] == 0) continue;
                var shop = found[index];
                float price = EconomyUtils.GetMarketPrice(shop.Resource, prefabs, entities);
                if (price <= 0f) continue;
                int stock = shop.Stock;
                for (int sale = 0; sale < counts[index] && stock > 0; sale++)
                {
                    int amount = (int)math.min(stock, perSale / price);
                    if (amount <= 0) break;
                    stock -= amount;
                    nextBuyer = (nextBuyer + 1) % sleepers.Count;
                    __instance.m_SalesQueue.Enqueue(new ResourceBuyerSystem.SalesEvent
                    {
                        m_Flags = ResourceBuyerSystem.SaleFlags.CommercialSeller | ResourceBuyerSystem.SaleFlags.Virtual,
                        m_Buyer = sleepers[nextBuyer],
                        m_Seller = shop.Entity,
                        m_Resource = shop.Resource,
                        m_Amount = amount,
                    });
                    sales++;
                    spent += (long)(amount * price);
                }
            }
            LastSales = sales;
            LastSpent = spent;
        }

        private static void MakeQueries(EntityManager entities)
        {
            if (queriesMade) return;
            queriesMade = true;
            awakeHouseholds = entities.CreateEntityQuery(ComponentType.ReadOnly<Household>(), ComponentType.ReadOnly<PropertyRenter>(),
                ComponentType.Exclude<TouristHousehold>(), ComponentType.Exclude<CommuterHousehold>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
            shops = entities.CreateEntityQuery(ComponentType.ReadOnly<CommercialCompany>(), ComponentType.ReadOnly<PropertyRenter>(), ComponentType.ReadOnly<PrefabRef>(),
                ComponentType.ReadOnly<CompanyStatisticData>(), ComponentType.ReadOnly<Game.Economy.Resources>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
        }

        /// <summary>What the sleepers spend in an hour: the awake households' spending on their last full day, per household.</summary>
        private static long HourlySpending(int sleeping)
        {
            using (var households = awakeHouseholds.ToComponentDataArray<Household>(Allocator.Temp))
            {
                if (households.Length == 0) return 0;
                long total = 0;
                foreach (var household in households) total += household.m_ShoppedValueLastDay;
                return total * sleeping / households.Length / 24;
            }
        }

        private static List<Shop> Shops(EntityManager entities, out long totalWeight)
        {
            var found = new List<Shop>();
            totalWeight = 0;
            using (var companies = shops.ToEntityArray(Allocator.Temp))
            using (var prefabs = shops.ToComponentDataArray<PrefabRef>(Allocator.Temp))
            using (var statistics = shops.ToComponentDataArray<CompanyStatisticData>(Allocator.Temp))
                for (int index = 0; index < companies.Length; index++)
                {
                    if (!entities.HasComponent<IndustrialProcessData>(prefabs[index].m_Prefab)) continue;
                    var resource = entities.GetComponentData<IndustrialProcessData>(prefabs[index].m_Prefab).m_Output.m_Resource;
                    if (resource == Resource.NoResource || resource == Resource.Vehicles) continue;
                    int stock = EconomyUtils.GetResources(resource, entities.GetBuffer<Game.Economy.Resources>(companies[index], true));
                    if (stock <= 0) continue;
                    int weight = statistics[index].m_MonthlyCustomerCount + statistics[index].m_CurrentNumberOfCustomers + 1;
                    found.Add(new Shop { Entity = companies[index], Resource = resource, Weight = weight, Stock = stock });
                    totalWeight += weight;
                }
            return found;
        }
    }
}
