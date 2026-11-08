using System;
using System.Collections.Generic;
using System.Linq;
using GimmeCapture.Services.Core.Media;
using SkiaSharp;
using Xunit.Abstractions;
using static GimmeCapture.Tests.ScrollCaptureSimulation;

namespace GimmeCapture.Tests;

// Opt-in: runs simulated manual scrolling captures (ScrollCaptureSimulation) through ManualScrollStitcher and reports,
// per scenario, how many page rows it kept twice (dup), how many rows came from the app's fixed bars (bars: each bar
// once is right), how many rows some frame showed that it lost (lost), where the kept rows jump (breaks), and how many
// kept rows show something drawn over the page (dirty: a floating pill once at an end is right; animated content is
// dirty by nature). SCROLL_SIM=1 to run it; SCROLL_SIM=old also runs the stitching the app did before, for comparison;
// SCROLL_SIM_TRACE=<scenario> prints every step of one scenario.
//
// 2026-11-08, frames 500 rows tall unless named: the new stitcher kept every scenario whole with no duplicate row,
// except scrolling 440 and 700 rows a frame, past what its window (the frame less the 48-row edge guards) can follow,
// where it keeps the first frame and adds nothing rather than guess. The old one stopped growing under a fixed bar (535
// to 1,248 of 4,900 rows), repeated a message box (188 rows of bars instead of 80), kept 22 rows twice after a flick
// past the end and back, stopped at 732 rows scrolling up under a floating date pill, and at 700 rows a frame stitched a
// piece out of place; it kept up with 440 a frame, its window being the whole frame. Each new run took 17-21% of the
// old one's time (44-51% where drawing the frames dominates).
public sealed class ScrollCaptureSimulationProbeTests(ITestOutputHelper output)
{
    private const double Tolerance = 0.35; // SnipWindowViewModel.ManualRowMismatchTolerance
    private const int EdgeGuard = 48; // SnipWindowViewModel: 48 logical pixels, at 100%

    private sealed record Scenario(string Name, Func<Page> Page, List<int> Path, Overlays Overlays, int Height = 500);

    private static IEnumerable<Scenario> Scenarios()
    {
        Page Chat11() => Chat(600, 5600, seed: 11);
        Page Repeats() => Chat(600, 5600, seed: 31, repeatPercent: 70);
        yield return new("wheel down", Chat11, WheelPath(0, 4400, seed: 1, notchesPerFrame: 0.3), new Overlays());
        yield return new("wheel down fast", Chat11, WheelPath(0, 4400, seed: 2, notchesPerFrame: 1.5), new Overlays());
        yield return new("wheel down flick", Chat11, WheelPath(0, 4400, seed: 3, notchesPerFrame: 3), new Overlays());
        yield return new("steady 40", Chat11, SteadyPath(0, 4400, 40), new Overlays());
        yield return new("steady 200", Chat11, SteadyPath(0, 4400, 200), new Overlays());
        yield return new("wheel up", Chat11, WheelPath(4400, 0, seed: 4, notchesPerFrame: 0.5), new Overlays());
        yield return new("header 56 down", Chat11, WheelPath(0, 4400, seed: 5, notchesPerFrame: 0.5), new Overlays(HeaderRows: 56));
        yield return new("footer 80 down", Chat11, WheelPath(0, 4400, seed: 6, notchesPerFrame: 0.5), new Overlays(FooterRows: 80));
        yield return new("header 56 up", Chat11, WheelPath(4400, 0, seed: 7, notchesPerFrame: 0.5), new Overlays(HeaderRows: 56));
        yield return new("footer 80 up", Chat11, WheelPath(4400, 0, seed: 8, notchesPerFrame: 0.5), new Overlays(FooterRows: 80));
        yield return new("both bars down", Chat11, WheelPath(0, 4400, seed: 13, notchesPerFrame: 0.5), new Overlays(HeaderRows: 56, FooterRows: 80));
        yield return new("hover down", Chat11, WheelPath(0, 4400, seed: 9, notchesPerFrame: 0.5), new Overlays(HoverRow: 250));
        yield return new("jitter down", Chat11, WheelPath(0, 4400, seed: 10, notchesPerFrame: 0.5), new Overlays(Jitter: 4));
        yield return new("tall wheel down", Chat11, WheelPath(0, 4400, seed: 12, notchesPerFrame: 0.8), new Overlays(), Height: 900);
        yield return new("down and back", Chat11, DownAndBack(), new Overlays());
        yield return new("steady 440", Chat11, SteadyPath(0, 4400, 440), new Overlays());
        yield return new("steady 700", Chat11, SteadyPath(0, 4400, 700), new Overlays());
        yield return new("flick and back", Chat11, FlickAndBack(), new Overlays());
        yield return new("late footer down", Chat11, WheelPath(0, 4400, seed: 14, notchesPerFrame: 0.5), new Overlays(FooterRows: 80, FooterFromFrame: 30));
        yield return new("date pill up", Chat11, WheelPath(4400, 0, seed: 15, notchesPerFrame: 0.5), new Overlays(DatePill: true));
        yield return new("date pill down", Chat11, WheelPath(0, 4400, seed: 16, notchesPerFrame: 0.5), new Overlays(DatePill: true));
        yield return new("animated down", Chat11, WheelPath(0, 4400, seed: 17, notchesPerFrame: 0.5), new Overlays(AnimatedTop: 1200, AnimatedRows: 220));
        foreach (int seed in new[] { 41, 42, 43 })
        {
            yield return new($"repeats {seed}", Repeats, WheelPath(0, 4400, seed, notchesPerFrame: 0.7), new Overlays());
        }
    }

    [OptInFact("SCROLL_SIM")]
    public void Report()
    {
        bool old = Environment.GetEnvironmentVariable("SCROLL_SIM") == "old";
        output.WriteLine($"{"scenario",-18} {"algo",-4} {"frames",6} {"kept",6} {"dup",5} {"bars",5} {"lost",5} {"breaks",6} {"dirty",6} {"ms",6}");
        foreach (Scenario scenario in Scenarios())
        {
            using Page page = scenario.Page();
            ReportRun(scenario, "new", page, () => RunNew(page, scenario, step: null));
            if (old)
            {
                ReportRun(scenario, "old", page, () => RunOld(page, scenario));
            }
        }
    }

    // SCROLL_SIM=1 too: which way the Auto direction locks, the old way (whole frames, the edge overhung by 8 rows) and
    // ManualScrollAxisDetector's, and at which frame. Sideways scenarios turn every frame a quarter clockwise.
    [OptInFact("SCROLL_SIM")]
    public void AxisReport()
    {
        var cases = new List<(string Name, Func<Page> Page, List<int> Path, Overlays Overlays, int Width, int Height, bool Sideways)>
        {
            ("chat down", () => Chat(600, 5600, seed: 11), WheelPath(0, 1500, seed: 1, notchesPerFrame: 0.5), new Overlays(), 600, 500, false),
            ("chat bars down", () => Chat(600, 5600, seed: 11), WheelPath(0, 1500, seed: 5, notchesPerFrame: 0.5), new Overlays(HeaderRows: 56, FooterRows: 80), 600, 500, false),
            ("line down", () => Line(545, 5000, seed: 3), WheelPath(0, 1500, seed: 31, notchesPerFrame: 0.5), new Overlays(), 545, 731, false),
            ("line bars down", () => Line(545, 5000, seed: 3), WheelPath(0, 1500, seed: 32, notchesPerFrame: 0.5), new Overlays(HeaderRows: 170), 545, 731, false),
            ("line bars up", () => Line(545, 5000, seed: 4), WheelPath(3000, 1500, seed: 33, notchesPerFrame: 0.5), new Overlays(HeaderRows: 170), 545, 731, false),
            ("line bars pill up", () => Line(545, 5000, seed: 5), WheelPath(3000, 1500, seed: 34, notchesPerFrame: 0.5), new Overlays(HeaderRows: 170, DatePill: true), 545, 731, false),
            ("line all bars down", () => Line(545, 5000, seed: 6), WheelPath(0, 1500, seed: 35, notchesPerFrame: 0.5), new Overlays(HeaderRows: 170, FooterRows: 60), 545, 731, false),
            ("line bars slow down", () => Line(545, 5000, seed: 7), WheelPath(0, 1500, seed: 36, notchesPerFrame: 0.15), new Overlays(HeaderRows: 170), 545, 731, false),
            ("chat sideways", () => Chat(600, 5600, seed: 11), WheelPath(0, 1500, seed: 37, notchesPerFrame: 0.5), new Overlays(), 600, 500, true),
            ("line sideways", () => Line(545, 5000, seed: 8), WheelPath(0, 1500, seed: 38, notchesPerFrame: 0.5), new Overlays(), 545, 731, true),
            ("line bars sideways", () => Line(545, 5000, seed: 9), WheelPath(0, 1500, seed: 39, notchesPerFrame: 0.5), new Overlays(HeaderRows: 120), 545, 731, true),
        };

        output.WriteLine($"{"scenario",-20} {"want",-10} {"old",-16} {"new",-16}");
        foreach (var c in cases)
        {
            using Page page = c.Page();
            SKBitmap Grab(int i)
            {
                SKBitmap frame = Frame(page, c.Path[i], c.Height, c.Overlays, i);
                return c.Sideways ? Sideways(frame) : frame;
            }

            using SKBitmap first = Grab(0);
            int screenWidth = first.Width;
            int screenHeight = first.Height;
            int minOverlapV = Math.Max(8, screenHeight / 10);
            int ignoreRightV = Math.Clamp(180, 24, Math.Max(24, screenWidth * 35 / 100));
            int minOverlapH = Math.Max(8, screenWidth / 10);
            int ignoreRightH = Math.Clamp(32, 8, Math.Max(8, screenHeight / 8));

            string old = "none";
            using (SKBitmap firstTurned = ScrollStitcher.RotateCcw90(first))
            {
                for (int i = 1; i < c.Path.Count && old == "none"; i++)
                {
                    using SKBitmap frame = Grab(i);
                    using SKBitmap frameTurned = ScrollStitcher.RotateCcw90(frame);
                    int growV = Overhang(ScrollStitcher.AlignFrameToStrip(first, frame, minOverlapV, ignoreRightV, Tolerance, frame.Height), first.Height, frame.Height);
                    int growH = Overhang(ScrollStitcher.AlignFrameToStrip(firstTurned, frameTurned, minOverlapH, ignoreRightH, Tolerance, frameTurned.Height), firstTurned.Height, frameTurned.Height);
                    int dominant = Math.Max(growV, growH);
                    if (dominant >= 8 && dominant >= Math.Min(growV, growH) * 2)
                    {
                        old = $"{(growH > growV ? "SIDEWAYS" : "down")} @{i}";
                    }
                }
            }

            string decided = "none";
            var detector = new ManualScrollAxisDetector(minOverlapV, ignoreRightV, minOverlapH, ignoreRightH, Tolerance);
            for (int i = 1; i < c.Path.Count && decided == "none"; i++)
            {
                using SKBitmap frame = Grab(i);
                bool? axis = detector.Decide(first, frame);
                if (axis != null)
                {
                    decided = $"{(axis == true ? "SIDEWAYS" : "down")} @{i}";
                }
            }

            output.WriteLine($"{c.Name,-20} {(c.Sideways ? "SIDEWAYS" : "down"),-10} {old,-16} {decided,-16}");
        }
    }

    private static int Overhang(ScrollStitcher.FrameAlignment align, int stripHeight, int frameHeight) =>
        !align.Found ? 0
        : align.Offset < 0 ? -align.Offset
        : align.Offset + frameHeight > stripHeight ? align.Offset + frameHeight - stripHeight
        : 0;

    [OptInFact("SCROLL_SIM_TRACE")]
    public void TraceSteps()
    {
        string name = Environment.GetEnvironmentVariable("SCROLL_SIM_TRACE")!;
        Scenario scenario = Scenarios().Single(s => s.Name == name);
        using Page page = scenario.Page();
        (RowTrace trace, SKBitmap result) = RunNew(page, scenario, (i, step, trace) => output.WriteLine(
            $"{i,3} top={scenario.Path[i],5} moved={scenario.Path[i] - scenario.Path[i - 1],4} placed={step.Placed} " +
            $"off={step.Offset} guided={step.Guided} near={step.Predicted} " +
            $"best={(step.BestScore == double.MaxValue ? "MAX" : step.BestScore.ToString("F3"))} " +
            $"+{step.Prepended}/+{step.Appended} bands={step.TopBand}/{step.BottomBand} " +
            $"trim={step.TrimmedTop}/{step.TrimmedBottom} " +
            $"lost={(trace.Started ? trace.Missing(scenario.Path.Take(i + 1).ToList()) : 0)}"));
        result.Dispose();
    }

    private void ReportRun(Scenario scenario, string algorithm, Page page, Func<(RowTrace Trace, SKBitmap Result)> run)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        (RowTrace trace, SKBitmap result) = run();
        long ms = watch.ElapsedMilliseconds;
        using (result)
        {
            output.WriteLine(
                $"{scenario.Name,-18} {algorithm,-4} {scenario.Path.Count,6} {result.Height,6} {trace.DuplicatedRows,5} " +
                $"{trace.OverlayRows,5} {trace.Missing(scenario.Path),5} {trace.Breaks,6} {trace.DirtyRows(result, page),6} {ms,6}");
        }
    }

    private static List<int> DownAndBack()
    {
        var path = WheelPath(0, 2600, seed: 21, notchesPerFrame: 0.6);
        path.AddRange(WheelPath(2600, 1500, seed: 22, notchesPerFrame: 0.6).Skip(1));
        path.AddRange(WheelPath(1500, 4400, seed: 23, notchesPerFrame: 0.6).Skip(1));
        return path;
    }

    private static List<int> FlickAndBack()
    {
        var path = WheelPath(0, 1200, seed: 24, notchesPerFrame: 0.6);
        path.AddRange(SteadyPath(1200, 3000, 600).Skip(1));
        path.AddRange(WheelPath(3000, 1400, seed: 25, notchesPerFrame: 0.6).Skip(1));
        path.AddRange(WheelPath(1400, 4400, seed: 26, notchesPerFrame: 0.6).Skip(1));
        return path;
    }

    private static (int MinOverlap, int IgnoreRight) Parameters(int width, int height) =>
        (Math.Max(8, height / 10), Math.Clamp(180, 24, Math.Max(24, width * 35 / 100)));

    private static (RowTrace Trace, SKBitmap Result) RunNew(Page page, Scenario scenario, Action<int, ManualScrollStep, RowTrace>? step)
    {
        List<int> tops = scenario.Path;
        (int minOverlap, int ignoreRight) = Parameters(page.Width, scenario.Height);
        using SKBitmap seed = Frame(page, tops[0], scenario.Height, scenario.Overlays);
        using var stitcher = new ManualScrollStitcher(seed, minOverlap, ignoreRight, Tolerance, EdgeGuard);
        var trace = new RowTrace(scenario.Height, scenario.Overlays);
        for (int i = 1; i < tops.Count; i++)
        {
            ManualScrollStep placed = stitcher.Add(Frame(page, tops[i], scenario.Height, scenario.Overlays, i));
            if (!trace.Started && stitcher.Started)
            {
                // The strip started with this frame, cut with the bands this step reports.
                trace.Start(tops[0], placed.TopBand, placed.BottomBand);
            }

            if (trace.Started)
            {
                trace.Apply(placed, tops[i], i, tops[i - 1], i - 1);
            }

            step?.Invoke(i, placed, trace);
        }

        if (!trace.Started)
        {
            trace.Start(tops[0], 0, 0);
        }

        return (trace, stitcher.ToBitmap());
    }

    // The stitching SnipWindowViewModel.ApplyStitchFrame did before ManualScrollStitcher, step for step: the whole strip
    // searched for each frame, a placement vetoed only when it disagreed with the motion by a third of a frame.
    private static (RowTrace Trace, SKBitmap Result) RunOld(Page page, Scenario scenario)
    {
        List<int> tops = scenario.Path;
        int height = scenario.Height;
        (int minOverlap, int ignoreRight) = Parameters(page.Width, height);
        SKBitmap previous = Frame(page, tops[0], height, scenario.Overlays);
        using var strip = new ManualScrollStrip(previous, ignoreRight);
        var trace = new RowTrace(height, scenario.Overlays);
        trace.Start(tops[0], 0, 0);
        long motion = 0;
        bool motionValid = true;
        int vetoes = 0;
        int lastOffset = 0;
        for (int i = 1; i < tops.Count; i++)
        {
            SKBitmap frame = Frame(page, tops[i], height, scenario.Overlays, i);
            ScrollStitcher.VerticalShift shift = ScrollStitcher.FindVerticalShift(previous, frame, minOverlap, ignoreRight, Tolerance);
            if (shift.Found)
            {
                motion += shift.Rows;
            }
            else
            {
                motionValid = false;
            }

            ScrollStitcher.FrameAlignment align = strip.Align(frame, minOverlap, Tolerance, height, out _, out _, out _);
            bool vetoed = false;
            if (align.Found && motionValid
                && Math.Abs(align.Offset - lastOffset - motion) > Math.Max(height / 3, 120))
            {
                vetoed = true;
                if (++vetoes >= 10)
                {
                    motionValid = false;
                }
            }

            if (align.Found && !vetoed)
            {
                int stripHeight = strip.Height;
                if (align.Offset < 0)
                {
                    strip.Prepend(frame, -align.Offset);
                    trace.Prepended(tops[i], -align.Offset, i);
                }
                else if (align.Offset + height > stripHeight)
                {
                    int rows = align.Offset + height - stripHeight;
                    strip.Append(frame, rows);
                    trace.Appended(tops[i], rows, i);
                }

                lastOffset = align.Offset < 0 ? 0 : align.Offset;
                motion = 0;
                motionValid = true;
                vetoes = 0;
            }

            previous.Dispose();
            previous = frame;
        }

        previous.Dispose();
        return (trace, strip.ToLogicalBitmap());
    }
}
