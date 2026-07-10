using System.Runtime.Versioning;
using Tcfc.Core;

namespace Tcfc.Cli;

/// <summary>
/// v1 hardware-verification harness: live monitoring (EC reads via PawnIO)
/// and board-gated fan-mode control (firmware WMI). Run from an elevated
/// terminal; the manifest requests elevation when launched directly.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (EcUnavailableException ex)
        {
            Console.Error.WriteLine(
                $"EC not available: {ex.Message}. Is PawnIO installed and are you running as Administrator?");
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
        string command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
        switch (command)
        {
            case "help" or "-h" or "--help" or "/?":
                PrintUsage();
                return 0;
            case "monitor":
                return Monitor();
            case "mode" when args.Length == 1:
                return ShowMode();
            case "mode":
                return SetMode(args[1]);
            default:
                Console.Error.WriteLine($"unknown command '{args[0]}'");
                PrintUsage();
                return 1;
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            ThinkCentre Fan Control - v1 hardware harness (run from an elevated terminal)

            usage:
              Tcfc.Cli monitor                            live RPM + EC temps + fan mode until a key is pressed
              Tcfc.Cli mode                               show current and supported fan modes
              Tcfc.Cli mode <quiet|balanced|performance>  set the fan mode (verified board only), then read back

            exit codes: 0 ok, 1 error, 2 EC/driver unavailable, 3 refused (unsupported board)
            """);
    }

    /// <summary>
    /// Prints one refreshing status line roughly every second until a key is
    /// pressed: fan RPM, the raw EC temperature block (0x21..0x2F, -1 marks a
    /// timed-out offset), and the firmware fan mode.
    /// </summary>
    private static int Monitor()
    {
        bool interactive = !Console.IsInputRedirected;
        bool refreshInPlace = !Console.IsOutputRedirected;
        Console.WriteLine(interactive
            ? "monitoring - press any key to stop"
            : "monitoring - Ctrl+C to stop");

        using var ec = new EcReader();
        int lineWidth = 0;
        while (true)
        {
            int rpm = ec.Rpm(); // -1 = no reading (EC lock or handshake timeout)
            int[] temps = ec.Temps();
            string mode;
            try
            {
                mode = FanModes.Get().ToString();
            }
            catch
            {
                mode = "?"; // WMI mode read failed; EC monitoring still works.
            }

            string rpmText = rpm < 0 ? "-" : rpm.ToString();
            string line = $"RPM {rpmText} | temps[0x21..0x2F] {string.Join(' ', temps)} | mode {mode}";
            if (refreshInPlace)
            {
                lineWidth = Math.Max(lineWidth, line.Length);
                Console.Write("\r" + line.PadRight(lineWidth));
            }
            else
            {
                Console.WriteLine(line);
            }

            // Wait ~1 s until the next reading, reacting to a keypress fast.
            for (int slice = 0; slice < 20; slice++)
            {
                if (interactive && Console.KeyAvailable)
                {
                    Console.ReadKey(intercept: true);
                    if (refreshInPlace)
                        Console.WriteLine();
                    return 0;
                }
                Thread.Sleep(50);
            }
        }
    }

    private static int ShowMode()
    {
        Console.WriteLine($"mode {FanModes.Get()}");
        Console.WriteLine($"supported {string.Join(" ", FanModes.Supported())}");
        return 0;
    }

    private static int SetMode(string modeArg)
    {
        // Board gate first: firmware fan-mode writes happen only on the model
        // this was verified on; everything else stays read-only.
        string? product = Board.Product();
        if (!MachineGuard.IsSupportedBoard(product))
        {
            Console.WriteLine(
                "Fan mode control is only enabled on the verified model (board 3376). " +
                $"This machine reports board '{product ?? "(unknown)"}'. Monitoring still works.");
            return 3;
        }

        FanMode? mode = modeArg.ToLowerInvariant() switch
        {
            "quiet" => FanMode.Quiet,
            "balanced" => FanMode.Balanced,
            "performance" => FanMode.Performance,
            _ => null,
        };
        if (mode is null)
        {
            Console.Error.WriteLine($"unknown mode '{modeArg}' - use quiet, balanced or performance");
            return 1;
        }

        FanModes.Set(mode.Value);
        Console.WriteLine($"mode set -> {FanModes.Get()}");
        return 0;
    }
}
