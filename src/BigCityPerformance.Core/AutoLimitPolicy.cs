// Big City Performance. Original work by TevBenji, 2026-11. GPL-3.0.
using System;

namespace BigCityPerformance.Core
{
    /// <summary>
    /// Picks how many people to simulate in detail from how the clock is keeping up, so nobody has to guess a number for
    /// their computer. Every <see cref="CheckSeconds"/> of play it looks at the window just gone, and at the last
    /// <see cref="Typical"/> of them:
    /// <list type="bullet">
    /// <item>The clock behind over those windows (under 90 % of the speed asked for, at most 2x) and fewer people might help
    /// (the clock waited on routes, or the processor, not the graphics card, set the pace): a fifth fewer.</item>
    /// <item>As many windows after a cut: unless the game got at least <see cref="NeededGain"/> faster, the cut comes back and
    /// no more cuts for <see cref="QuietAfterUselessCut"/> checks. In a million-person city the processor stayed busy with
    /// everything else and cuts alone took the limit to the floor for no speed (test run, 8 November 2026).</item>
    /// <item>Below <see cref="Start"/>, after <see cref="StepUpAfter"/> checks at one limit: a step back up, kept unless the
    /// limit below was <see cref="NeededGain"/> faster. A cut kept by chance doesn't stay for good, and on the top speed button,
    /// where the clock is rarely on time, the limit can come back up at all.</item>
    /// <item>The clock on time with room to spare (no waiting on routes): a tenth more, but not within
    /// <see cref="QuietChecks"/> checks of a cut, so it doesn't see-saw.</item>
    /// </list>
    /// Between <see cref="Floor"/> and <see cref="Ceiling"/>, in whole thousands.
    /// </summary>
    public sealed class AutoLimitPolicy
    {
        public const int Floor = 20000;
        public const int Ceiling = 500000;
        public const int Start = 50000;
        public const float CheckSeconds = 20f;

        /// <summary>Checks after a cut before the limit may rise again.</summary>
        public const int QuietChecks = 3;

        /// <summary>
        /// Checks without cuts after a cut that didn't help (five minutes), twice as long after each one in a row, up to
        /// <see cref="MostQuietAfterUselessCut"/>. In a three-million city the cost isn't in the awake people, and retrying every
        /// five minutes only churned households for nothing (a player's logs, 9 November 2026).
        /// </summary>
        public const int QuietAfterUselessCut = 15;

        /// <summary>The longest wait after useless cuts (80 minutes).</summary>
        public const int MostQuietAfterUselessCut = 240;

        /// <summary>
        /// How much faster the game has to run with fewer people for a cut to stay: a tenth. From one minute to the next at the
        /// same limit a player's city swung by up to 7 %, and at 5 % a cut that changed nothing still passed now and then: the
        /// limit went down and, on the top speed button, never came back (20 November 2026).
        /// </summary>
        public const float NeededGain = 1.1f;

        /// <summary>Checks at one limit below <see cref="Start"/> before a step back up (five minutes), twice as long after each
        /// step up that was undone, up to <see cref="MostStepUpWait"/>.</summary>
        public const int StepUpAfter = 15;

        /// <summary>The longest wait between steps up (80 minutes).</summary>
        public const int MostStepUpWait = 240;

        /// <summary>The fastest speed it aims for: the third speed button is "as fast as it goes", not a promise of 4x.</summary>
        public const float MostSpeed = 2f;

        private enum Change { None, Cut, StepUp }

        private int sinceCut = QuietChecks, noCuts, uselessCuts;
        private Change judging;
        private int limitBefore, atLimit, stepUpWait = StepUpAfter;
        private float ratioBefore, speedAtChange;

        /// <summary>Checks averaged into the usual speed (a minute).</summary>
        public const int Typical = 3;
        private readonly float[] recent = new float[Typical];
        private int recentCount;
        private float recentSpeed;

        public int Limit { get; private set; } = Start;

        /// <summary>
        /// How the last cut or step up was judged, once, for the log: the minute after it against the minute before. A player's
        /// limit still slid to the floor in an hour, and the log showed the cuts but not why each was kept (19 November 2026).
        /// </summary>
        public string LastJudgement { get; private set; }

        /// <summary>Hands over <see cref="LastJudgement"/> and forgets it.</summary>
        public string TakeJudgement()
        {
            string judgement = LastJudgement;
            LastJudgement = null;
            return judgement;
        }

        /// <summary>
        /// The player picked another speed, even for a moment between checks: what came before doesn't compare with what comes
        /// next, so the minute starts over, and a change being judged stays, as with a change <see cref="Update"/> sees itself.
        /// </summary>
        public void SpeedChanged()
        {
            recentCount = 0;
            judging = Change.None;
        }

        /// <param name="achieved">Game speed reached in the window (1 = normal).</param>
        /// <param name="selected">The speed the player picked.</param>
        /// <param name="stalledShare">Share of frames the clock waited on route deadlines.</param>
        /// <param name="cpuBound">The processor set the frame's pace (or it couldn't be told).</param>
        /// <returns>True when the limit changed.</returns>
        public bool Update(float achieved, float selected, float stalledShare, bool cpuBound)
        {
            if (selected <= 0f) return false;
            float ratio = achieved / Math.Min(selected, MostSpeed);
            if (selected != recentSpeed) { recentSpeed = selected; recentCount = 0; }
            recent[recentCount++ % Typical] = ratio;
            if (sinceCut < QuietChecks) sinceCut++;
            if (noCuts > 0) noCuts--;
            atLimit++;
            // A minute's average, before a change and after it, not one window: a cut follows a slow window and the next one is
            // usually faster by chance alone. Judged window against window, cuts that changed nothing kept passing and walked a
            // 296,000-person city down to the floor for no speed (a player's three hours, 16 November 2026).
            float usual = (recent[0] + recent[1] + recent[2]) / Typical;
            string needed = $"{NeededGain - 1f:P0}";
            if (judging != Change.None)
            {
                // Judged only at the speed it was made at: after a speed change the two don't compare, so the change stays.
                if (selected != speedAtChange) judging = Change.None;
                else if (recentCount < Typical) return false;
                else if (judging == Change.Cut)
                {
                    judging = Change.None;
                    bool helped = usual >= ratioBefore * NeededGain;
                    LastJudgement = $"{(helped ? "kept the cut to" : "undid the cut to")} {Limit:N0}: the minute after it ran at {usual:P0} of the speed asked for, "
                        + $"the minute before {ratioBefore:P0}{(helped ? "" : $" (it needs {needed} more)")}";
                    if (!helped)
                    {
                        noCuts = Math.Min(QuietAfterUselessCut << uselessCuts, MostQuietAfterUselessCut);
                        if (noCuts < MostQuietAfterUselessCut) uselessCuts++;
                        recentCount = 0;
                        return Set(limitBefore);
                    }
                    // One step back, not all the way: even a minute can look faster by chance.
                    if (uselessCuts > 0) uselessCuts--;
                }
                else
                {
                    judging = Change.None;
                    // More people awake is the point, so the step up stays unless the limit below was clearly faster.
                    bool slower = usual * NeededGain < ratioBefore;
                    LastJudgement = $"{(slower ? "undid the step up to" : "kept the step up to")} {Limit:N0}: the minute after it ran at {usual:P0} of the speed asked for, "
                        + $"the minute before {ratioBefore:P0}{(slower ? $" (fewer people ran {needed} faster)" : "")}";
                    if (slower)
                    {
                        stepUpWait = Math.Min(stepUpWait * 2, MostStepUpWait);
                        recentCount = 0;
                        return Set(limitBefore);
                    }
                    stepUpWait = StepUpAfter;
                }
            }
            if (recentCount >= Typical && usual < 0.9f && (stalledShare > 0.1f || cpuBound) && noCuts == 0 && Limit > Floor)
            {
                sinceCut = 0;
                return Begin(Change.Cut, usual, selected, Limit * 4 / 5);
            }
            if (ratio >= 0.97f && stalledShare < 0.02f && sinceCut >= QuietChecks) return Set(Limit * 11 / 10);
            if (Limit < Start && recentCount >= Typical && atLimit >= stepUpWait) return Begin(Change.StepUp, usual, selected, Math.Min(Start, Limit * 5 / 4));
            return false;
        }

        // A cut or a step up, judged on the minute after it.
        private bool Begin(Change change, float usual, float selected, int limit)
        {
            int before = Limit;
            if (!Set(limit)) return false;
            judging = change;
            limitBefore = before;
            ratioBefore = usual;
            speedAtChange = selected;
            recentCount = 0;
            return true;
        }

        public void Reset()
        {
            Limit = Start;
            sinceCut = QuietChecks;
            noCuts = 0;
            uselessCuts = 0;
            judging = Change.None;
            recentCount = 0;
            atLimit = 0;
            stepUpWait = StepUpAfter;
        }

        private bool Set(int limit)
        {
            limit = Math.Max(Floor, Math.Min(Ceiling, (limit + 500) / 1000 * 1000));
            if (limit == Limit) return false;
            Limit = limit;
            atLimit = 0;
            return true;
        }
    }
}
