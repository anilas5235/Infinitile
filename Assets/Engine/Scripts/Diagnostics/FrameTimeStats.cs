using System.Collections.Generic;
using System.Globalization;
using Unity.Mathematics;

namespace Engine.Scripts.Diagnostics{
    /// <summary>
    ///     Statistics of frame times (or of any other per-frame milliseconds): average, percentiles, maximum, and
    ///     the frames longer than a frame of 120 and of 60 frames per second. The pipeline tests and the goal run
    ///     of a player build (<see cref="GoalRunner" />) compute them the same way.
    /// </summary>
    internal static class FrameTimeStats{
        /// <summary>A frame of 120 frames per second, the goal of the performance work.</summary>
        internal const float GoalFrameMs = 1000f / 120f;

        /// <summary>A frame of 60 frames per second; a longer one is a stutter the player sees.</summary>
        internal const float StutterFrameMs = 1000f / 60f;

        /// <summary>The statistics of a range of samples.</summary>
        internal struct Summary{
            internal int Count;
            internal double Avg;
            internal float P50, P95, P99, Max;

            /// <summary>Samples longer than <see cref="GoalFrameMs" /> and longer than <see cref="StutterFrameMs" />.</summary>
            internal int OverGoal, OverStutter;

            internal float OverGoalShare => Count > 0 ? (float)OverGoal / Count : 0f;
            internal float OverStutterShare => Count > 0 ? (float)OverStutter / Count : 0f;
        }

        /// <summary>
        ///     The statistics of the samples <paramref name="from" /> (included) to <paramref name="to" /> (excluded),
        ///     clamped to the list. The percentile of a sorted range of n samples is the sample at index
        ///     (int)((n - 1) * q). Allocates a sorted copy of the range.
        /// </summary>
        internal static Summary Summarize(List<float> values, int from, int to){
            from = math.clamp(from, 0, values.Count);
            to = math.clamp(to, from, values.Count);
            Summary summary = new(){ Count = to - from };
            if (summary.Count == 0) return summary;

            List<float> sorted = values.GetRange(from, to - from);
            sorted.Sort();
            double sum = 0;
            foreach (float value in sorted){
                sum += value;
                if (value > GoalFrameMs) summary.OverGoal++;
                if (value > StutterFrameMs) summary.OverStutter++;
            }

            int last = sorted.Count - 1;
            summary.Avg = sum / sorted.Count;
            summary.P50 = sorted[sorted.Count / 2];
            summary.P95 = sorted[(int)(last * 0.95f)];
            summary.P99 = sorted[(int)(last * 0.99f)];
            summary.Max = sorted[last];
            return summary;
        }

        /// <summary>"avg …, p50 …, p95 …, p99 …, max …" of the range, or "no samples".</summary>
        internal static string Distribution(List<float> values, int from, int to){
            Summary s = Summarize(values, from, to);
            if (s.Count == 0) return "no samples";
            return string.Format(CultureInfo.InvariantCulture,
                "avg {0:0.000}, p50 {1:0.000}, p95 {2:0.000}, p99 {3:0.000}, max {4:0.000}", s.Avg, s.P50, s.P95,
                s.P99, s.Max);
        }

        /// <summary>How many of the samples of the range are longer than a frame of 120 and of 60 frames per second.</summary>
        internal static string OverGoal(List<float> values, int from, int to){
            Summary s = Summarize(values, from, to);
            int count = math.max(s.Count, 1);
            return string.Format(CultureInfo.InvariantCulture,
                "{0} of {1} over 8.33 ms ({2:0.00} %), {3} over 16.67 ms ({4:0.00} %)",
                s.OverGoal, s.Count, 100f * s.OverGoal / count, s.OverStutter, 100f * s.OverStutter / count);
        }
    }
}
