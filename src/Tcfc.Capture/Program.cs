using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;
using Tcfc.Core;

namespace Tcfc.Capture;

/// <summary>
/// Dev-only capture tool that renders README marketing assets from live
/// hardware readings: a still monitor card (monitor.png) and an ~11 s
/// animated capture (demo.gif) that ramps the fan by loading every core
/// mid-recording. Every displayed number comes straight from the EC /
/// firmware WMI at the moment of the frame — nothing is fabricated.
/// Run from an elevated terminal on the verified machine; this project is
/// not part of the shipped tray release.
///
/// The `slim` subcommand is different: it re-encodes an existing animated
/// GIF smaller (fewer frames, smaller pixels) and touches no hardware, so it
/// needs neither elevation nor the EC driver.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class Program
{
    private const string StillName = "monitor.png";
    private const string GifName = "demo.gif";

    private const int SeedReads = 24;       // history seeding for the still (~3.5 s)
    private const int SeedIntervalMs = 150;

    private const int FrameCount = 110;     // ~11 s at ~10 fps
    private const int FrameDelayMs = 100;
    private const int LoadStartFrame = 15;  // idle lead-in before the ramp
    private const int LoadFrames = 60;      // ~6 s of full-core load, then settle
    private const int ModeReadEvery = 10;   // WMI is slow; refresh the mode ~1/s

    // slim mode: keep every 3rd frame, cap the width at 620 px and replay at
    // 150 ms/frame — still smooth for a README, at a fraction of the bytes.
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
        catch (EcUnavailableException ex)
        {
            Console.Error.WriteLine(
                $"EC not available: {ex.Message}. This tool needs Administrator and PawnIO — " +
                "run it from an elevated terminal on the machine with the driver installed.");
            return 2;
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

        using var ec = new EcReader();
        var renderer = new CardRenderer();

        string stillPath = Path.Combine(outDir, StillName);
        CaptureStill(ec, renderer, stillPath);
        Console.WriteLine($"wrote {stillPath}");

        string gifPath = Path.Combine(outDir, GifName);
        (int minRpm, int maxRpm) = CaptureGif(ec, renderer, gifPath);
        Console.WriteLine($"wrote {gifPath}");
        Console.WriteLine(minRpm <= maxRpm
            ? $"rpm observed during capture: min {minRpm}, max {maxRpm}"
            : "rpm observed during capture: no valid readings (EC returned -1 throughout)");
        return 0;
    }

    /// <summary>
    /// Default output directory is docs/screenshots under the repo root,
    /// found by walking up from the executable to the directory holding the
    /// solution file; an explicit first argument overrides it.
    /// </summary>
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

    /// <summary>
    /// `slim &lt;input.gif&gt; &lt;output.gif&gt;`: shrinks an animated GIF by
    /// keeping every <see cref="SlimFrameStride"/>rd frame, downscaling to at
    /// most <see cref="SlimMaxWidth"/> px wide and re-encoding at
    /// <see cref="SlimDelayMs"/> ms/frame. Input and output may be the same
    /// path; the result is then staged in a temp file and swapped in.
    /// </summary>
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
            // Image.FromFile keeps the file locked, so copy every kept frame
            // out into standalone bitmaps and dispose the source before any
            // writing happens (the output may be this same file).
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

            // For an in-place run, encode next to the target and swap it in so
            // a failed encode never destroys the original.
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

    /// <summary>
    /// Copies the image's active frame into a new 24-bpp bitmap no wider than
    /// <paramref name="maxWidth"/>, preserving the aspect ratio. The caller
    /// owns the returned bitmap.
    /// </summary>
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

    /// <summary>
    /// Renders the still card from the current readings, seeding the chart
    /// with a short run of real RPM samples first.
    /// </summary>
    private static void CaptureStill(EcReader ec, CardRenderer renderer, string path)
    {
        Console.WriteLine($"still: sampling rpm for ~{SeedReads * SeedIntervalMs / 1000.0:0.#} s...");
        var history = new List<int>(SeedReads);
        int rpm = -1;
        for (int i = 0; i < SeedReads; i++)
        {
            rpm = ec.Rpm();
            history.Add(rpm);
            if (i < SeedReads - 1)
                Thread.Sleep(SeedIntervalMs);
        }

        int? temp = TempSummary.Representative(ec.Temps());
        FanMode? mode = TryReadMode();

        using Bitmap card = renderer.Render(rpm, temp, mode, history);
        card.Save(path, ImageFormat.Png);
    }

    /// <summary>
    /// Captures ~11 s at ~10 fps: a short idle lead-in, ~6 s with every
    /// logical CPU loaded so the firmware ramps the fan, then the settle.
    /// Each frame is rendered from the readings taken at that instant.
    /// </summary>
    private static (int Min, int Max) CaptureGif(EcReader ec, CardRenderer renderer, string path)
    {
        Console.WriteLine($"gif: capturing {FrameCount} frames (~{FrameCount * FrameDelayMs / 1000} s), " +
                          $"cpu load frames {LoadStartFrame}-{LoadStartFrame + LoadFrames - 1}...");

        var frames = new List<Bitmap>(FrameCount);
        var history = new List<int>(CardRenderer.HistorySlots + 1);
        int min = int.MaxValue, max = int.MinValue;
        FanMode? mode = TryReadMode();
        var load = new CpuLoad();
        try
        {
            var clock = Stopwatch.StartNew();
            for (int i = 0; i < FrameCount; i++)
            {
                if (i == LoadStartFrame)
                {
                    load.Start();
                    Console.WriteLine("  cpu load ON");
                }
                if (i == LoadStartFrame + LoadFrames)
                {
                    load.Stop();
                    Console.WriteLine("  cpu load off, settling");
                }

                int rpm = ec.Rpm();
                int? temp = TempSummary.Representative(ec.Temps());
                if (i % ModeReadEvery == 0)
                    mode = TryReadMode();

                history.Add(rpm);
                if (history.Count > CardRenderer.HistorySlots)
                    history.RemoveAt(0);
                if (rpm >= 0)
                {
                    min = Math.Min(min, rpm);
                    max = Math.Max(max, rpm);
                }

                frames.Add(renderer.Render(rpm, temp, mode, history));

                // Pace frames against the wall clock so capture time matches
                // the GIF's declared timing.
                long nextDue = (long)(i + 1) * FrameDelayMs;
                int wait = (int)(nextDue - clock.ElapsedMilliseconds);
                if (wait > 0)
                    Thread.Sleep(wait);
            }

            Console.WriteLine("  encoding...");
            AnimatedGif.Save(path, frames, FrameDelayMs);
        }
        finally
        {
            load.Stop();
            foreach (Bitmap frame in frames)
                frame.Dispose();
        }
        return (min, max);
    }

    /// <summary>Firmware mode via WMI; null when the interface is unavailable.</summary>
    private static FanMode? TryReadMode()
    {
        try
        {
            return FanModes.Get();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Saturates every logical CPU with a tight math loop so the firmware
    /// ramps the fan during the capture. BelowNormal priority keeps the
    /// capture loop itself responsive while still consuming all idle cycles.
    /// </summary>
    private sealed class CpuLoad
    {
        private Thread[]? _threads;
        private volatile bool _stop;
        private double _sink; // keeps the loop's result observable

        public void Start()
        {
            if (_threads is not null)
                return;
            _stop = false;
            _threads = new Thread[Environment.ProcessorCount];
            for (int i = 0; i < _threads.Length; i++)
            {
                _threads[i] = new Thread(Spin)
                {
                    IsBackground = true,
                    Priority = ThreadPriority.BelowNormal,
                    Name = $"cpu-load-{i}",
                };
                _threads[i].Start();
            }
        }

        public void Stop()
        {
            Thread[]? threads = _threads;
            if (threads is null)
                return;
            _stop = true;
            foreach (Thread thread in threads)
                thread.Join(2000);
            _threads = null;
        }

        private void Spin()
        {
            double x = 1.000001;
            while (!_stop)
            {
                x = x * 1.0000001 + Math.Sqrt(x + 1.0) - Math.Sin(x);
                if (!double.IsFinite(x) || x > 1e12)
                    x = 1.000001;
            }
            _sink = x;
        }
    }
}
