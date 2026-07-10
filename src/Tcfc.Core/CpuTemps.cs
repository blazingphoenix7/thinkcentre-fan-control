namespace Tcfc.Core;

/// <summary>
/// Per-core CPU temperature via Intel MSRs through the PawnIO IntelMSR module.
/// Tjmax comes from MSR 0x1A2; IA32_THERM_STATUS (0x19C) gives each core's
/// offset below it. Read-only, like everything else in this app.
/// </summary>
public sealed class CpuTemps : IDisposable
{
    private const string ModuleFileName = "IntelMSR.bin";
    private const int MsrTemperatureTarget = 0x1A2;
    private const int MsrThermStatus = 0x19C;

    private IntPtr _handle;
    private int _tjmax = -1; // cached after the first read
    private bool _disposed;

    /// <summary>Opens PawnIO and loads the IntelMSR blob. Throws EcUnavailableException if the driver, blob or elevation is missing.</summary>
    public CpuTemps(string? modulePath = null)
    {
        string path = ResolveModulePath(modulePath);

        byte[] blob;
        try
        {
            blob = File.ReadAllBytes(path);
        }
        catch (Exception ex)
        {
            throw new EcUnavailableException($"Could not read PawnIO module '{path}': {ex.Message}", ex);
        }

        int hr;
        try
        {
            hr = PawnIoNative.pawnio_open(out _handle);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            throw new EcUnavailableException(
                @"PawnIOLib.dll could not be loaded. Is PawnIO installed (expected under C:\Program Files\PawnIO)?", ex);
        }

        if (hr != 0)
        {
            _handle = IntPtr.Zero;
            throw new EcUnavailableException(
                $"pawnio_open failed with HRESULT 0x{hr:X8}. Is the process elevated and the PawnIO driver running?");
        }

        hr = PawnIoNative.pawnio_load(_handle, blob, (UIntPtr)(uint)blob.Length);
        if (hr != 0)
        {
            PawnIoNative.pawnio_close(_handle);
            _handle = IntPtr.Zero;
            throw new EcUnavailableException($"pawnio_load of '{path}' failed with HRESULT 0x{hr:X8}.");
        }
    }

    /// <summary>Tjmax in C from MSR 0x1A2 bits 23:16.</summary>
    public static int DecodeTjmax(long msr) => (int)((msr >> 16) & 0xFF);

    /// <summary>
    /// Core temp in C from an IA32_THERM_STATUS value: bit 31 = reading valid,
    /// bits 22:16 = offset below Tjmax. -1 when the valid bit is clear.
    /// </summary>
    public static int DecodeCoreTempC(long msr, int tjmax)
        => ((msr >> 31) & 1) == 1 ? tjmax - (int)((msr >> 16) & 0x7F) : -1;

    /// <summary>Tjmax for this package; read once and cached.</summary>
    public int Tjmax()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_tjmax < 0)
            _tjmax = DecodeTjmax(ReadMsr(MsrTemperatureTarget));
        return _tjmax;
    }

    /// <summary>
    /// Temperature of every logical core in C; -1 marks an invalid or failed reading.
    /// Note: pins the calling thread's affinity to each core in turn (the MSR is
    /// per-core), so call this off the UI thread.
    /// </summary>
    public int[] PerCore()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int tjmax = Tjmax();

        int count = Environment.ProcessorCount;
        var temps = new int[count];
        IntPtr thread = PawnIoNative.GetCurrentThread();
        try
        {
            for (int core = 0; core < count; core++)
            {
                // An affinity mask covers one 64-processor group; anything past that reads -1.
                if (core >= 64 ||
                    PawnIoNative.SetThreadAffinityMask(thread, (UIntPtr)(1UL << core)) == UIntPtr.Zero)
                {
                    temps[core] = -1;
                    continue;
                }

                try
                {
                    temps[core] = DecodeCoreTempC(ReadMsr(MsrThermStatus), tjmax);
                }
                catch (EcUnavailableException)
                {
                    temps[core] = -1;
                }
            }
        }
        finally
        {
            // Back to all cores; without this the thread stays pinned to the last one.
            ulong all = count >= 64 ? ulong.MaxValue : (1UL << count) - 1;
            PawnIoNative.SetThreadAffinityMask(thread, (UIntPtr)all);
        }
        return temps;
    }

    // One MSR read on the current core.
    private long ReadMsr(int index)
    {
        var input = new long[] { index };
        var output = new long[1];
        int hr = PawnIoNative.pawnio_execute(
            _handle, "ioctl_read_msr", input, (UIntPtr)(uint)input.Length, output, (UIntPtr)1, out _);
        if (hr != 0)
            throw new EcUnavailableException($"pawnio_execute('ioctl_read_msr') failed with HRESULT 0x{hr:X8}.");
        return output[0];
    }

    private static string ResolveModulePath(string? modulePath)
    {
        if (modulePath is not null)
        {
            if (File.Exists(modulePath))
                return modulePath;
            throw new EcUnavailableException($"PawnIO module not found at '{modulePath}'.");
        }

        var candidates = new List<string>
        {
            // next to the exe (deployment layout)
            Path.Combine(AppContext.BaseDirectory, ModuleFileName),
        };

        // repo dev layout: walk up from the exe to the solution root
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (dir.GetFiles("*.sln").Length > 0)
            {
                candidates.Add(Path.Combine(dir.FullName, "lib", "pawnio", "modules", ModuleFileName));
                break;
            }
        }

        // a PawnIO install that ships its modules
        candidates.Add(Path.Combine(@"C:\Program Files\PawnIO\modules", ModuleFileName));

        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        throw new EcUnavailableException(
            "PawnIO MSR module IntelMSR.bin not found. Searched: " + string.Join("; ", candidates) +
            ". Place IntelMSR.bin next to the executable.");
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_handle != IntPtr.Zero)
        {
            try
            {
                PawnIoNative.pawnio_close(_handle);
            }
            catch
            {
                // Dispose must not throw.
            }
            _handle = IntPtr.Zero;
        }
    }
}
