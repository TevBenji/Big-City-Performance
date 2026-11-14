// Big City Performance. Original work by TevBenji, 2026-11. GPL-3.0.
using System.Collections.Generic;
using Game;
using Game.Serialization;
using Game.Simulation;
using HarmonyLib;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine.Scripting;

namespace BigCityPerformance.Services
{
    /// <summary>
    /// "Turn off pollution": the game's systems that make air, ground, noise and tap-water pollution are switched off, and the
    /// pollution a city already has is cleared once. Everything that reads pollution keeps running and reads clean maps:
    /// happiness, health, land value, rent, house hunting, zoning, trees. Nothing runs per frame, unlike cleaning up after
    /// the game every step (a player's city with a mod that did that: 14 ms of every 24 ms step on the main thread).
    /// </summary>
    public sealed partial class PollutionSwitch : GameSystemBase
    {
        // By class name, so MapExt's own copies of the three map systems are found too. The ground-water, tree and pollution
        // trigger systems stay on: with clean maps they add nothing, and MapExt reports any of them switched off as a fault.
        private static readonly HashSet<string> Makers = new HashSet<string>
        {
            nameof(AirPollutionSystem), nameof(GroundPollutionSystem), nameof(NoisePollutionSystem),
            nameof(BuildingPollutionAddSystem), nameof(NetPollutionSystem), nameof(WaterPipePollutionSystem),
            "AirPollutionSystemMod", "GroundPollutionSystemMod", "NoisePollutionSystemMod",
        };

        internal static PollutionSwitch Instance { get; private set; }

        // Only systems this mod switched off are switched back on: MapExt keeps the game's map systems off for its own copies.
        private readonly List<ComponentSystemBase> switchedOff = new List<ComponentSystemBase>();
        private List<ComponentSystemBase> makers;
        private EntityQuery roads, pipeNodes, pipeEdges;
        private bool inCity, clearOwed = true, noPollutionChecked;

        /// <summary>True while the game's pollution systems are switched off.</summary>
        internal bool Off => switchedOff.Count > 0;

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            Instance = this;
            roads = GetEntityQuery(ComponentType.ReadWrite<Game.Net.Pollution>());
            pipeNodes = GetEntityQuery(ComponentType.ReadWrite<WaterPipeNode>());
            pipeEdges = GetEntityQuery(ComponentType.ReadWrite<WaterPipeEdge>());
        }

        [Preserve]
        protected override void OnUpdate()
        {
            if (inCity && Mod.Settings.TurnOffPollution)
            {
                // Checked every frame, not only when the setting changes: another mod may switch its copy back on at a load.
                foreach (var system in makers ??= FindMakers())
                {
                    if (!system.Enabled) continue;
                    system.Enabled = false;
                    if (!switchedOff.Contains(system)) switchedOff.Add(system);
                }
                if (clearOwed)
                {
                    clearOwed = false;
                    Clear();
                }
                return;
            }
            clearOwed = true;
            if (switchedOff.Count == 0) return;
            // Cars kept adding to the roads' pollution while nothing used it up: without this the first update would pour all
            // of it into the noise map at once.
            if (inCity) ClearRoads();
            foreach (var system in switchedOff) system.Enabled = true;
            switchedOff.Clear();
            Mod.Logger.Info("Pollution switched back on");
        }

        [Preserve]
        protected override void OnGameLoadingComplete(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            base.OnGameLoadingComplete(purpose, mode);
            // The main menu and the editors switch it back on.
            inCity = mode == GameMode.Game;
            // A save carries its city's pollution. Another mod may have added its copies since the last city.
            clearOwed = true;
            makers = null;
            if (inCity && !noPollutionChecked)
            {
                noPollutionChecked = true;
                foreach (var system in World.Systems)
                {
                    if (!(system.GetType().Namespace ?? "").StartsWith("NoPollution", System.StringComparison.Ordinal)) continue;
                    Mod.Logger.Info("The No Pollution mod is loaded. Turn off pollution, in this mod's options, stops pollution without work on every simulation step, so No Pollution can be removed.");
                    break;
                }
            }
        }

        /// <summary>Before a save: so a save never carries the roads' backlog to a game without this mod.</summary>
        internal void BeforeSave()
        {
            if (Off) ClearRoads();
        }

        private List<ComponentSystemBase> FindMakers()
        {
            var found = new List<ComponentSystemBase>();
            foreach (var system in World.Systems)
                if (Makers.Contains(system.GetType().Name)) found.Add(system);
            // The game's own system is left alone where MapExt runs a copy: MapExt keeps it off, and turning pollution back on
            // must never start it beside the copy.
            found.RemoveAll(system => found.Exists(copy => copy.GetType().Name == system.GetType().Name + "Mod"));
            Mod.Logger.Info("Pollution switched off: " + string.Join(", ", found.ConvertAll(system => system.GetType().Name)));
            return found;
        }

        private void Clear()
        {
            ClearMap(World.GetOrCreateSystemManaged<AirPollutionSystem>());
            ClearMap(World.GetOrCreateSystemManaged<GroundPollutionSystem>());
            ClearMap(World.GetOrCreateSystemManaged<NoisePollutionSystem>());
            // MapExt sends GetMap on the game's systems to its own copies, so these reach whichever map is in use.
            var groundWater = World.GetOrCreateSystemManaged<GroundWaterSystem>();
            var water = groundWater.GetMap(false, out var dependencies);
            dependencies.Complete();
            for (int index = 0; index < water.Length; index++)
            {
                var cell = water[index];
                if (cell.m_Polluted == 0) continue;
                cell.m_Polluted = 0;
                water[index] = cell;
            }
            ClearPipes();
            ClearRoads();
            Mod.Logger.Info("Pollution cleared");
        }

        private static void ClearMap<T>(CellMapSystem<T> system) where T : struct, Colossal.Serialization.Entities.ISerializable
        {
            var map = system.GetMap(false, out var dependencies);
            dependencies.Complete();
            for (int index = 0; index < map.Length; index++) map[index] = default;
        }

        private void ClearPipes()
        {
            Rewrite(pipeNodes, (ref WaterPipeNode node) =>
            {
                if (node.m_FreshPollution == 0f) return false;
                node.m_FreshPollution = 0f;
                return true;
            });
            Rewrite(pipeEdges, (ref WaterPipeEdge edge) =>
            {
                if (edge.m_FreshPollution == 0f) return false;
                edge.m_FreshPollution = 0f;
                return true;
            });
        }

        private void ClearRoads() => Rewrite(roads, (ref Game.Net.Pollution road) =>
        {
            if (!math.any(road.m_Pollution != 0f) && !math.any(road.m_Accumulation != 0f)) return false;
            road = default;
            return true;
        });

        private delegate bool Cleaner<T>(ref T value);

        // Written back only when something changed: writing marks every chunk changed for the game's change filters.
        private static void Rewrite<T>(EntityQuery query, Cleaner<T> clean) where T : unmanaged, IComponentData
        {
            var values = query.ToComponentDataArray<T>(Allocator.Temp);
            bool changed = false;
            for (int index = 0; index < values.Length; index++)
            {
                var value = values[index];
                if (!clean(ref value)) continue;
                values[index] = value;
                changed = true;
            }
            if (changed) query.CopyFromComponentDataArray(values);
            values.Dispose();
        }
    }

    /// <summary>Road pollution is cleared before each save while pollution is off (<see cref="PollutionSwitch.BeforeSave"/>).</summary>
    [HarmonyPatch(typeof(SaveGameSystem), nameof(SaveGameSystem.RunOnce))]
    internal static class PollutionSavePatch
    {
        private static void Prefix() => PollutionSwitch.Instance?.BeforeSave();
    }
}
