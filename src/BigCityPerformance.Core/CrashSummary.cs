// Big City Performance. Original work by TevBenji, 2026-11. GPL-3.0.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace BigCityPerformance.Core
{
    /// <summary>
    /// After a crash, the next start writes what happened to this mod's log, so a player's report is one file: when the game
    /// crashed, how long after the city loaded, where the game was (its own report in the previous Player.log), and which of
    /// this mod's options and which mods were on. A native crash ends the game at once, nothing can be written then, so the
    /// mod keeps a short diary of the session as it goes (<see cref="Line"/>) and reads it back at the next start.
    /// </summary>
    public static class CrashSummary
    {
        public const string Started = "game started", CityLoaded = "city loaded", Options = "options:", Mods = "mods:", Closed = "game closed";

        private const string TimeFormat = "yyyy-MM-dd HH:mm:ss";

        // The game writes a few lines after the mods close (memory statistics), seconds, not minutes.
        private const double SameSessionMinutes = 2;

        /// <summary>One diary line: the local time, then what happened.</summary>
        public static string Line(DateTime time, string text) => time.ToString(TimeFormat, CultureInfo.InvariantCulture) + " " + text;

        /// <summary>The summary to log, or null if the previous session didn't crash.</summary>
        public static string Build(string previousPlayerLog, DateTime crashTime, string diary)
        {
            if (string.IsNullOrEmpty(previousPlayerLog)) return null;
            bool native = previousPlayerLog.Contains("Native Crash Reporting");
            if (!native && !previousPlayerLog.Contains("Crash!!!")) return null;

            DateTime? loaded = null, closed = null;
            string options = null, mods = null;
            foreach (var raw in (diary ?? "").Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (line.Length <= TimeFormat.Length
                    || !DateTime.TryParseExact(line.Substring(0, TimeFormat.Length), TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
                    continue;
                string text = line.Substring(TimeFormat.Length + 1);
                // Read even a moment past the log's last write: the game's last lines and this one can share a second.
                if (text.StartsWith(Closed, StringComparison.Ordinal)) closed = time;
                if (time > crashTime) continue;
                if (text.StartsWith(CityLoaded, StringComparison.Ordinal)) loaded = time;
                else if (text.StartsWith(Options, StringComparison.Ordinal)) options = text.Substring(Options.Length).Trim();
                else if (text.StartsWith(Mods, StringComparison.Ordinal)) mods = text.Substring(Mods.Length).Trim();
            }
            // The game can write a crash report and carry on (14 November 2026: one at start-up, while it set up its entity
            // types). If the diary closed normally as the log stopped, it's that session, and it didn't crash.
            if (closed.HasValue && Math.Abs((crashTime - closed.Value).TotalMinutes) < SameSessionMinutes) return null;
            // A diary that closed normally long before the log stopped is from an earlier run, and the crashed one ran without
            // this mod (turned off, then on again). Its details would describe the wrong session.
            bool diaryClosed = closed.HasValue;
            if (diaryClosed) { loaded = null; options = null; mods = null; }

            var frames = Frames(previousPlayerLog);
            var summary = new StringBuilder();
            summary.Append("The game crashed last time: ").Append(crashTime.ToString("d MMMM yyyy HH:mm", CultureInfo.InvariantCulture));
            if (loaded.HasValue)
            {
                var after = crashTime - loaded.Value;
                summary.Append(", ").Append((int)after.TotalMinutes).Append(" min ").Append(after.Seconds).Append(" s after the city loaded");
            }
            else if (!diaryClosed) summary.Append(", before a city was loaded");
            summary.Append(native ? " (a crash in the game's native code)." : ".");
            if (frames.Any(frame => frame.Contains("EntityCommandBuffer")))
                summary.Append("\nWhere: while the game applied a batch of queued changes to the city. That is usually broken data from a mod or an asset reaching the game, more than a fault in the code that ran.");
            if (diaryClosed) summary.Append("\nBig City Performance has no notes from that session, so it probably wasn't running then.");
            if (options != null) summary.Append("\nBig City Performance's options then: ").Append(options).Append('.');
            if (mods != null) summary.Append("\nMods loaded then: ").Append(mods).Append('.');
            if (frames.Count > 0) summary.Append("\nThe game's own report (Player-prev.log):\n  ").Append(string.Join("\n  ", frames));
            summary.Append("\nTo report it: Options, Big City Performance, Report a bug, with this log.");
            return summary.ToString();
        }

        // The first frames of the game's managed stack, without addresses and the runtime's own glue.
        private static List<string> Frames(string log)
        {
            var frames = new List<string>();
            int start = log.IndexOf("Managed Stacktrace:", StringComparison.Ordinal);
            if (start < 0) return frames;
            foreach (var raw in log.Substring(start).Split('\n').Skip(1))
            {
                string line = raw.Trim();
                if (line.StartsWith("=====", StringComparison.Ordinal))
                {
                    if (frames.Count > 0) break;
                    continue;
                }
                if (!line.StartsWith("at ", StringComparison.Ordinal)) continue;
                int address = line.IndexOf(" <0x", StringComparison.Ordinal);
                if (address > 0) line = line.Substring(0, address);
                if (line.Contains("<unknown>") || line.Contains("wrapper_native") || line.Contains("<Module>:") || line.Contains("runtime_invoke")) continue;
                frames.Add(line.Substring(3));
                if (frames.Count == 12) break;
            }
            return frames;
        }
    }
}
