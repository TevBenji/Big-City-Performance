// Big City Performance. Original work by TevBenji, 2026-11. GPL-3.0.
using System;
using System.IO;
using System.Linq;
using BigCityPerformance.Core;
using Game;
using Game.SceneFlow;
using UnityEngine;
using UnityEngine.Scripting;

namespace BigCityPerformance.Diagnostics
{
    /// <summary>
    /// The crash check (<see cref="CrashSummary"/>): at each start, if the last session crashed, its summary goes into this
    /// mod's log. While playing, a short diary in ModsData records the city loads, this mod's options as they change, and the
    /// mods loaded. Nothing leaves the player's PC.
    /// </summary>
    public sealed partial class CrashCheck : GameSystemBase
    {
        private const float CheckSeconds = 2f;

        private static string Diary => Path.Combine(Application.persistentDataPath, "ModsData", nameof(BigCityPerformance), "session.txt");

        private string lastOptions;
        private float nextCheck;
        private bool inCity, modsWritten;

        /// <summary>At mod load, before this session writes anything: the last session's summary if it crashed, then a new diary.</summary>
        internal static void ReportLastSession()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Diary));
            string previousLog = Path.Combine(Application.persistentDataPath, "Player-prev.log");
            if (File.Exists(previousLog))
            {
                string summary = CrashSummary.Build(ReadShared(previousLog), File.GetLastWriteTime(previousLog), File.Exists(Diary) ? ReadShared(Diary) : "");
                if (summary != null) Mod.Logger.Warn(summary);
            }
            File.WriteAllText(Diary, CrashSummary.Line(DateTime.Now, $"{CrashSummary.Started}, Big City Performance {typeof(Mod).Assembly.GetName().Version}") + "\n");
        }

        [Preserve]
        protected override void OnUpdate()
        {
            if (!inCity || UnityEngine.Time.realtimeSinceStartup < nextCheck) return;
            nextCheck = UnityEngine.Time.realtimeSinceStartup + CheckSeconds;
            string options = OptionsNow();
            if (options == lastOptions) return;
            lastOptions = options;
            Write($"{CrashSummary.Options} {options}");
        }

        [Preserve]
        protected override void OnGameLoadingComplete(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            base.OnGameLoadingComplete(purpose, mode);
            inCity = mode == GameMode.Game;
            if (!inCity) return;
            Write(CrashSummary.CityLoaded);
            lastOptions = OptionsNow();
            Write($"{CrashSummary.Options} {lastOptions}");
            if (modsWritten) return;
            modsWritten = true;
            Write($"{CrashSummary.Mods} {LoadedMods()}");
        }

        private static string OptionsNow()
        {
            var settings = Mod.Settings;
            if (settings == null) return "unknown";
            string limit = !settings.LimitPeople ? "off" : settings.AutoLimit ? "automatic" : $"{settings.PeopleInDetail} thousand";
            return $"keep the clock running {OnOff(settings.KeepClockRunning)}, extra steps {OnOff(settings.ExtraSteps)}, people limit {limit}, "
                + $"turn off pollution {OnOff(settings.TurnOffPollution)}, water pipes without the wait {OnOff(settings.WaterNoWait)}";
        }

        private static string OnOff(bool on) => on ? "on" : "off";

        // Every code mod the game loaded, by assembly name and version.
        private static string LoadedMods()
        {
            try
            {
                var names = GameManager.instance.modManager.Where(mod => mod.isLoaded).Select(mod =>
                {
                    var name = new System.Reflection.AssemblyName(mod.assemblyFullName);
                    return $"{name.Name} {name.Version}";
                }).OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
                return $"{names.Count}: {string.Join(", ", names)}";
            }
            catch (Exception exception)
            {
                return "unknown (" + exception.Message + ")";
            }
        }

        private static void Write(string text)
        {
            try { File.AppendAllText(Diary, CrashSummary.Line(DateTime.Now, text) + "\n"); }
            catch (Exception exception) { Mod.Logger.Warn("The crash check couldn't write its session diary: " + exception.Message); }
        }

        // The game may still hold a log open.
        private static string ReadShared(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream)) return reader.ReadToEnd();
        }
    }
}
