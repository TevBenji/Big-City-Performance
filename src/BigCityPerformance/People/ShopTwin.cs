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
        private static double pickOffset;
        private static EntityQuery awakeHouseholds, shops;
        private static bool queriesMade;

        // This hour's buyers and their goods at home before the sale, put back once the game has settled the sales (see Postfix).
        private static readonly List<(Entity, int)> buyers = new List<(Entity, int)>();

        /// <summary>Last hour's sales and money, for the test runner.</summary>
        internal static int LastSales;
        internal static long LastSpent;

        private struct Shop
        {
            public Entity Entity;
            public Resource Resource;
            public int Weight, Stock;
        }

        /// <summary>
        /// The households asleep longest have woken and left the front of the list: the buyers' turn moves with it, so the
        /// next sale goes to the household after the last buyer, not to one further along (code review, 10 November 2026). It
        /// can go to -1, before the first: when the last buyer itself left, the next sale goes to the new first household.
        /// </summary>
        internal static void LeftTheFront(int count) => nextBuyer = System.Math.Max(-1, nextBuyer - count);

        /// <summary>
        /// Households the game removed leave the list from anywhere in it (<see cref="DormancySystem"/>'s WakeRemoved): the turn
        /// moves back by those at or before the last buyer, so the next sale still goes to the household after it (code review,
        /// 27 November 2026). Called before they leave the list.
        /// </summary>
        internal static void LeaveTheList(List<Entity> sleepers, HashSet<Entity> gone)
        {
            int before = 0;
            for (int index = 0; index <= nextBuyer && index < sleepers.Count; index++)
                if (gone.Contains(sleepers[index])) before++;
            LeftTheFront(before);
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
            // Where the picks land moves on each hour (by the golden ratio, which never repeats), so when there are fewer sales
            // than shops every shop still gets its share over the hours (code review, 8 November 2026).
            pickOffset = (pickOffset + 0.6180339887) % 1.0;
            var counts = ShareOut.Counts(weights, saleCount, pickOffset);
            long perSale = budget / saleCount;
            int sales = 0, visited = 0;
            long spent = 0;
            bool noBuyerLeft = false;
            var charged = new HashSet<Entity>();
            for (int index = 0; index < found.Count && !noBuyerLeft; index++)
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
                    Entity buyer = NextBuyerWhoCanPay(entities, sleepers, (long)(amount * price), charged, ref visited);
                    // Nobody left who can pay this much: the next shop's sales may be cheaper (a shop low on stock sells less).
                    if (buyer == Entity.Null) { noBuyerLeft = visited >= 2 * sleepers.Count; break; }
                    stock -= amount;
                    __instance.m_SalesQueue.Enqueue(new ResourceBuyerSystem.SalesEvent
                    {
                        m_Flags = ResourceBuyerSystem.SaleFlags.CommercialSeller | ResourceBuyerSystem.SaleFlags.Virtual,
                        m_Buyer = buyer,
                        m_Seller = shop.Entity,
                        m_Resource = shop.Resource,
                        m_Amount = amount,
                    });
                    if (entities.HasComponent<Household>(buyer)) buyers.Add((buyer, entities.GetComponentData<Household>(buyer).m_Resources));
                    sales++;
                    spent += (long)(amount * price);
                }
            }
            LastSales = sales;
            LastSpent = spent;
        }

        /// <summary>
        /// Sleepers use up what they buy. A sale moves its price from the buyer's money to its goods at home, and the game uses goods
        /// up only for households awake, so every purchase a sleeper made stayed in its home: days of shopping, counted in the
        /// Household Wealth chart (a player's chart went from 50 million to 2.4 billion, 21 November 2026). So each buyer's goods go
        /// back to what they were before the sale: the money is spent, as an awake household's goods would have been used up. Only
        /// what the sale added: goods count with money when the game decides whether a household can afford to stay, and taking
        /// all of them moved out tens of thousands of households in debt in a test city (22 November 2026).
        /// </summary>
        private static void Postfix(ResourceBuyerSystem __instance)
        {
            if (buyers.Count == 0) return;
            if (!DormancySystem.DevKeepGoods)
            {
                // The sales are settled by the job the system has just scheduled, not yet known to the other systems.
                ((JobHandle)DependencyGetter.Invoke(__instance, null)).Complete();
                var entities = __instance.EntityManager;
                foreach (var (buyer, goodsBefore) in buyers)
                {
                    if (!entities.Exists(buyer) || !entities.HasComponent<Household>(buyer)) continue;
                    var household = entities.GetComponentData<Household>(buyer);
                    // A sale the game turned down (the shop sold out) added nothing.
                    if (household.m_Resources <= goodsBefore) continue;
                    household.m_Resources = goodsBefore;
                    entities.SetComponentData(buyer, household);
                }
            }
            buyers.Clear();
        }

        /// <summary>
        /// The next sleeping household in turn that has the money and hasn't bought this hour, or none after a whole round. The
        /// game takes a sale's money even from a household without it, so charged regardless, sleepers ran into debt and the game
        /// moved them out (a player's 300,000-person city, 19 November 2026). Awake households only shop with money to spend.
        /// </summary>
        private static Entity NextBuyerWhoCanPay(EntityManager entities, IReadOnlyList<Entity> sleepers, long cost, HashSet<Entity> charged, ref int visited)
        {
            // ponytail: at most two rounds of the sleepers a game hour, so a cheaper sale after a dear one can still find a buyer
            // without the search growing with the number of sales.
            for (int tries = 0; tries < sleepers.Count && visited < 2 * sleepers.Count; tries++)
            {
                visited++;
                nextBuyer = (nextBuyer + 1) % sleepers.Count;
                var household = sleepers[nextBuyer];
                if (charged.Contains(household)) continue;
                if (DormancySystem.DevOldShopping || (entities.Exists(household) && entities.HasBuffer<Game.Economy.Resources>(household)
                    && EconomyUtils.GetResources(Resource.Money, entities.GetBuffer<Game.Economy.Resources>(household, true)) >= cost))
                {
                    charged.Add(household);
                    return household;
                }
            }
            return Entity.Null;
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
