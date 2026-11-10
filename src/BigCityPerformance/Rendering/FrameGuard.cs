// Big City Performance. Original work by TevBenji, 2026-11. GPL-3.0.
using BigCityPerformance.Core;
using BigCityPerformance.Settings;
using Game;
using Game.Rendering;
using Game.SceneFlow;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Scripting;

namespace BigCityPerformance.Rendering
{
    /// <summary>
    /// Holds the frame rate in a city the graphics card can't draw fast enough. A simulation that keeps up doesn't help when the
    /// picture itself comes at 14 frames a second, as a three-million city did on a laptop graphics card with the game paused
    /// (test run, 9 November 2026). Every <see cref="WindowSeconds"/> it looks at the frame rate and at what set the pace, and
    /// <see cref="DetailPolicy"/> lowers or gives back the game's own level of detail (Options, Graphics), which decides how
    /// soon distant things are drawn simpler or left out. The player's own setting is the most it ever draws, and it comes
    /// back when the option is turned off or the mod unloads.
    /// </summary>
    public sealed partial class FrameGuard : GameSystemBase
    {
        private const float WindowSeconds = 2f;

        internal static FrameGuard Instance;

        private readonly DetailPolicy policy = new DetailPolicy();
        private readonly FrameTiming[] timing = new FrameTiming[1];
        private RenderingSystem rendering;

        // The player's own level, and the level this last set (below zero: nothing set).
        private float own, applied = -1f;
        private float windowStart = -1f;
        private int frames, timed;
        private double main, graphics;

#if BCP_DEV
        /// <summary>The test runner's comparison: the hold on or off whatever the setting says.</summary>
        internal static bool? DevOn;
#endif

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            Instance = this;
            rendering = World.GetOrCreateSystemManaged<RenderingSystem>();
        }

        [Preserve]
        protected override void OnUpdate()
        {
            var game = GameManager.instance;
            bool on = Mod.Settings != null && Mod.Settings.HoldFrameRate;
#if BCP_DEV
            if (DevOn.HasValue) on = DevOn.Value;
#endif
            if (!on || game == null || game.isGameLoading || game.gameMode != GameMode.Game)
            {
                Restore();
                return;
            }
            // Any level but the one this set is the player's own: the first look, or a change in Options.
            float level = rendering.levelOfDetail;
            if (applied < 0f || math.abs(level - applied) > 0.0001f)
            {
                own = level;
                Apply();
            }
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (windowStart < 0f) StartWindow(now);
            frames++;
            FrameTimingManager.CaptureFrameTimings();
            if (FrameTimingManager.GetLatestTimings(1, timing) > 0 && timing[0].gpuFrameTime > 0)
            {
                main += timing[0].cpuMainThreadFrameTime;
                graphics += timing[0].gpuFrameTime;
                timed++;
            }
            if (now - windowStart < WindowSeconds) return;
            float framesPerSecond = frames / (now - windowStart);
            // Without timings (some graphics drivers give none) it can't tell what set the pace, and leaves the detail alone.
            bool graphicsBound = timed > 0 && graphics > main * 1.1;
            if (policy.Update(framesPerSecond, graphicsBound))
            {
                Apply();
                Mod.Logger.Info($"Level of detail at {policy.Factor:P0} of your setting: {framesPerSecond:F0} frames a second, {(graphicsBound ? "graphics card" : "processor")} setting the pace.");
            }
            StartWindow(now);
        }

        private void StartWindow(float now)
        {
            windowStart = now;
            frames = timed = 0;
            main = graphics = 0;
        }

        private void Apply()
        {
            applied = own * policy.Factor;
            rendering.levelOfDetail = applied;
            LiveStatus.SetDetail(policy.Factor);
        }

        /// <summary>The player's own level of detail back, if this changed it.</summary>
        internal void Restore()
        {
            if (applied < 0f) return;
            // Only if the level is still the one this set: a change made in Options since stays.
            if (math.abs(rendering.levelOfDetail - applied) <= 0.0001f) rendering.levelOfDetail = own;
            applied = -1f;
            windowStart = -1f;
            policy.Reset();
            LiveStatus.SetDetail(1f);
        }
    }
}
