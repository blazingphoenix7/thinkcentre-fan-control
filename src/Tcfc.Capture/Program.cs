using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.Versioning;
using Tcfc.Core;

namespace Tcfc.Capture;

/// <summary>
/// Dev-only tool that renders the README demo GIF. Headless: no EC, no PawnIO,
/// no elevation, no live hardware at all. The "RPM climbing" story is just a
/// table of numbers this file builds (an eased ramp up and back down, so the
/// gif loops) and hands to CardRenderer one frame at a time. The slim
/// subcommand re-encodes an existing gif smaller and is unrelated to any of
/// that - it just shrinks whatever file you point it at.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class Program
{
    private const string GifName = "demo.gif";
    private const string FrameCheckName = "_frame_check.png";

    private const int CoreCount = 10;
    private const int Tjmax = 105;

    private const int IdleRpm = 900;
    private const int PeakRpm = 3970;
    private const int IdleTempC = 48;
    private const int PeakTempC = 92;

    // Ping-pong: ease up over UpFrames steps, then ease back down over
    // DownFrames without repeating the peak or trough frame, so the loop has
    // no stutter or held duplicate at either end. Each 880-wide frame is a full
    // independent image in the stream (~240 KB), so the frame count is the main
    // lever on file size; 26 keeps the eased ramp smooth while landing the gif
    // well under GitHub's image-proxy ceiling.
    private const int UpFrames = 14;
    private const int DownFrames = UpFrames - 2;
    private const int FrameDelayMs = 80;
    private const int HistoryCapacity = 60; // matches DashboardForm's sparkline queue depth

    // rpm near which the verification frame should land, while Full Speed is active.
    private const int FrameCheckTargetRpm = 3900;

    // Small, fixed per-core phase offsets so the ten cores don't heat in
    // perfect lockstep - not random: the animation must render identically
    // every run.
    private static readonly float[] CorePhaseOffset =
    {
        0f, 0.025f, -0.03f, 0.012f, -0.02f, 0.03f, -0.012f, 0.02f, -0.025f, 0.008f,
    };

    private const int SlimFrameStride = 3;
    private const int SlimMaxWidth = 620;
    private const int SlimDelayMs = 150;

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length > 0 && string.Equals(args[0], "slim", StringComparison.OrdinalIgnoreCase))
                return Slim(args);
            return Run(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        string outDir = ResolveOutputDirectory(args);
        Directory.CreateDirectory(outDir);

        var renderer = new CardRenderer();
        List<Frame> timeline = BuildTimeline();

        string gifPath = Path.Combine(outDir, GifName);
        SaveGif(renderer, timeline, gifPath);
        Console.WriteLine($"wrote {gifPath}  ({timeline.Count} frames, {FrameDelayMs} ms each, {CardRenderer.Width}x{CardRenderer.Height})");

        string checkPath = Path.Combine(outDir, FrameCheckName);
        Frame checkFrame = PickFrameCheck(timeline);
        SaveFrameCheck(renderer, checkFrame, checkPath);
        Console.WriteLine($"wrote {checkPath}  (rpm {checkFrame.Rpm}, mode {(FanSelection)checkFrame.ModeIndex})");

        return 0;
    }

    // docs/screenshots under the repo root by default; first arg overrides
    private static string ResolveOutputDirectory(string[] args)
    {
        if (args.Length > 0)
            return Path.GetFullPath(args[0]);

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "thinkcentre-fan-control.sln")))
                return Path.Combine(dir.FullName, "docs", "screenshots");
        }

        throw new InvalidOperationException(
            "Repository root (thinkcentre-fan-control.sln) not found above the executable; " +
            "pass an output directory as the first argument.");
    }

    // One point in the animation: everything CardRenderer.Render needs for one frame.
    private readonly record struct Frame(int Rpm, int[] CoreTemps, int ModeIndex, int[] History);

    private static List<Frame> BuildTimeline()
    {
        int total = UpFrames + DownFrames;
        var progress = new float[total];
        for (int i = 0; i < UpFrames; i++)
        {
            float t = UpFrames == 1 ? 0f : (float)i / (UpFrames - 1);
            progress[i] = Ease(t);
        }
        for (int j = 0; j < DownFrames; j++)
        {
            // walks back from just-below-peak to just-above-trough, so the
            // peak and trough each appear exactly once in the whole loop
            int mirrorIndex = UpFrames - 2 - j;
            float t = (float)mirrorIndex / (UpFrames - 1);
            progress[UpFrames + j] = Ease(t);
        }

        var frames = new List<Frame>(total);
        var history = new List<int>(HistoryCapacity);
        for (int k = 0; k < total; k++)
        {
            float p = progress[k];
            int rpm = (int)MathF.Round(Lerp(IdleRpm, PeakRpm, p));

            var coreTemps = new int[CoreCount];
            for (int c = 0; c < CoreCount; c++)
            {
                float corePhase = CorePhaseOffset[c % CorePhaseOffset.Length];
                float coreProgress = Math.Clamp(p + corePhase, 0f, 1f);
                coreTemps[c] = (int)MathF.Round(Lerp(IdleTempC, PeakTempC, coreProgress));
            }

            history.Add(rpm);
            if (history.Count > HistoryCapacity)
                history.RemoveAt(0);

            frames.Add(new Frame(rpm, coreTemps, ModeIndexFor(p), history.ToArray()));
        }
        return frames;
    }

    // Quiet -> Balanced -> Performance -> Full Speed as the ramp climbs, and
    // back down the same way as it eases back - a pure function of progress,
    // so the mode reads consistently on both the rise and the fall.
    private static int ModeIndexFor(float progress) => progress switch
    {
        >= 0.80f => 3, // Full Speed
        >= 0.55f => 2, // Performance
        >= 0.25f => 1, // Balanced
        _ => 0,        // Quiet
    };

    private static float Ease(float t) => (1f - MathF.Cos(MathF.PI * t)) / 2f;

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static void SaveGif(CardRenderer renderer, List<Frame> timeline, string path)
    {
        var bitmaps = new List<Bitmap>(timeline.Count);
        try
        {
            foreach (Frame f in timeline)
                bitmaps.Add(renderer.Render(f.Rpm, f.CoreTemps, f.ModeIndex, f.History, Tjmax));
            AnimatedGif.Save(path, bitmaps, FrameDelayMs);
        }
        finally
        {
            foreach (Bitmap b in bitmaps)
                b.Dispose();
        }
    }

    // The frame closest to FrameCheckTargetRpm while Full Speed is active -
    // i.e. near the peak of the ramp, per the brief.
    private static Frame PickFrameCheck(List<Frame> timeline)
    {
        Frame best = timeline[0];
        int bestDiff = int.MaxValue;
        foreach (Frame f in timeline)
        {
            if (f.ModeIndex != 3)
                continue;
            int diff = Math.Abs(f.Rpm - FrameCheckTargetRpm);
            if (diff < bestDiff)
            {
                bestDiff = diff;
                best = f;
            }
        }
        return best;
    }

    private static void SaveFrameCheck(CardRenderer renderer, Frame frame, string path)
    {
        using Bitmap bmp = renderer.Render(frame.Rpm, frame.CoreTemps, frame.ModeIndex, frame.History, Tjmax);
        bmp.Save(path, ImageFormat.Png);
    }

    private static int Slim(string[] args)
    {
        if (args.Length != 3)
        {
            Console.Error.WriteLine("usage: Tcfc.Capture slim <input.gif> <output.gif>");
            return 1;
        }

        string inputPath = Path.GetFullPath(args[1]);
        string outputPath = Path.GetFullPath(args[2]);
        if (!File.Exists(inputPath))
        {
            Console.Error.WriteLine($"input not found: {inputPath}");
            return 1;
        }

        long inputBytes = new FileInfo(inputPath).Length;
        int inputFrames;
        var frames = new List<Bitmap>();
        try
        {
            // Image.FromFile keeps the file locked, so copy the kept frames out
            // and dispose the source before writing (output may be this file).
            using (Image source = Image.FromFile(inputPath))
            {
                var time = new FrameDimension(source.FrameDimensionsList[0]);
                inputFrames = source.GetFrameCount(time);
                for (int i = 0; i < inputFrames; i += SlimFrameStride)
                {
                    source.SelectActiveFrame(time, i);
                    frames.Add(Downscale(source, SlimMaxWidth));
                }
            }

            // In-place: stage next to the target and swap, so a failed encode
            // never destroys the original.
            bool inPlace = string.Equals(inputPath, outputPath, StringComparison.OrdinalIgnoreCase);
            string stagePath = inPlace ? outputPath + ".tmp" : outputPath;
            try
            {
                AnimatedGif.Save(stagePath, frames, SlimDelayMs);
                if (inPlace)
                    File.Move(stagePath, outputPath, overwrite: true);
            }
            finally
            {
                if (inPlace && File.Exists(stagePath))
                    File.Delete(stagePath);
            }
        }
        finally
        {
            foreach (Bitmap frame in frames)
                frame.Dispose();
        }

        long outputBytes = new FileInfo(outputPath).Length;
        Console.WriteLine($"in:  {inputFrames,4} frames  {inputBytes,12:N0} bytes  {inputPath}");
        Console.WriteLine($"out: {frames.Count,4} frames  {outputBytes,12:N0} bytes  {outputPath}");
        Console.WriteLine($"     {outputBytes * 100.0 / inputBytes:0.#}% of the input size");
        return 0;
    }

    private static Bitmap Downscale(Image source, int maxWidth)
    {
        int width = source.Width;
        int height = source.Height;
        if (width > maxWidth)
        {
            height = Math.Max(1, (int)Math.Round((double)height * maxWidth / width));
            width = maxWidth;
        }

        var scaled = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using Graphics g = Graphics.FromImage(scaled);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(source, new Rectangle(0, 0, width, height));
        return scaled;
    }
}
