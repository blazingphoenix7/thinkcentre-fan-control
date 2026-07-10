namespace Tcfc.Core;

/// <summary>
/// Read-only EC access via the signed PawnIO driver and its LpcACPIEC module:
/// the standard ACPI RD_EC handshake on ports 0x62/0x66 (see work/ec-probe.ps1).
/// Deliberately has no EC write path.
/// </summary>
public sealed class EcReader : IDisposable
{
    // ACPI embedded controller interface (fixed by the spec).
    private const int EcData = 0x62;          // data port
    private const int EcStatusCommand = 0x66; // status (read) / command (write) port
    private const int Obf = 0x01;             // output buffer full: a byte is ready on 0x62
    private const int Ibf = 0x02;             // input buffer full: EC hasn't taken our last write
    private const int RdEc = 0x80;            // command: read EC RAM byte

    // System-wide EC lock shared with firmware and other EC users. Taken per
    // read transaction only; holding it longer would starve everyone else.
    private const string EcMutexName = @"Global\Access_EC";
    private const int EcMutexTimeoutMs = 500;

    // Verified board's EC map: tach at 0x00 (hi) / 0x01 (lo), temps at 0x21..0x2F.
    private const int RpmHighOffset = 0x00;
    private const int RpmLowOffset = 0x01;
    private const int TempFirstOffset = 0x21;
    private const int TempLastOffset = 0x2F;

    private IntPtr _handle;
    private Mutex? _ecMutex;
    private bool _disposed;

    /// <summary>Opens PawnIO and loads the LpcACPIEC blob. Throws EcUnavailableException if the driver, blob or elevation is missing.</summary>
    public EcReader(string? modulePath = null)
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

        // Open or create the shared lock; only acquired per read transaction.
        // Best effort: without a handle, reads just proceed unlocked.
        try
        {
            _ecMutex = Mutex.OpenExisting(EcMutexName);
        }
        catch
        {
            try
            {
                _ecMutex = new Mutex(initiallyOwned: false, EcMutexName);
            }
            catch
            {
                _ecMutex = null;
            }
        }
    }

    /// <summary>One byte of EC RAM, under the shared lock. -1 on lock or EC timeout.</summary>
    public int ReadByte(int offset)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (offset is < 0 or > 0xFF)
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "EC RAM offsets are 0x00..0xFF.");

        if (!TryEnterEcLock())
            return -1;
        try
        {
            return ReadByteUnlocked(offset);
        }
        finally
        {
            ExitEcLock();
        }
    }

    /// <summary>Fan RPM from the tach pair, both bytes under one lock hold. -1 when unavailable.</summary>
    public int Rpm()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!TryEnterEcLock())
            return -1;
        int hi, lo;
        try
        {
            hi = ReadByteUnlocked(RpmHighOffset);
            lo = ReadByteUnlocked(RpmLowOffset);
        }
        finally
        {
            ExitEcLock();
        }
        return MachineGuard.RpmOrNull(hi, lo) ?? -1;
    }

    /// <summary>Raw temp block bytes 0x21..0x2F; -1 marks a timed-out offset.</summary>
    public int[] Temps()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var temps = new int[TempLastOffset - TempFirstOffset + 1];
        if (!TryEnterEcLock())
        {
            Array.Fill(temps, -1);
            return temps;
        }
        try
        {
            for (int i = 0; i < temps.Length; i++)
                temps[i] = ReadByteUnlocked(TempFirstOffset + i);
        }
        finally
        {
            ExitEcLock();
        }
        return temps;
    }

    // RD_EC handshake for one byte. Caller holds the EC lock. -1 on timeout.
    private int ReadByteUnlocked(int offset)
    {
        // Drain any stale byte a timed-out transaction (ours or someone
        // else's) left in the output buffer. The EC only ever has one pending.
        for (int i = 0; i < 16 && (PioRead(EcStatusCommand) & Obf) != 0; i++)
            PioRead(EcData);

        if (!WaitFlag(Ibf, set: false))
            return -1;
        PioWrite(EcStatusCommand, RdEc);
        if (!WaitFlag(Ibf, set: false))
            return -1;
        PioWrite(EcData, offset);
        if (!WaitFlag(Obf, set: true))
            return -1;
        return PioRead(EcData);
    }

    // False = another EC user held the lock past the timeout; the caller must
    // report "unavailable" rather than touch the ports unlocked.
    private bool TryEnterEcLock()
    {
        if (_ecMutex is null)
            return true;
        try
        {
            return _ecMutex.WaitOne(EcMutexTimeoutMs);
        }
        catch (AbandonedMutexException)
        {
            // Previous holder died; the lock is ours and the handshake re-syncs the EC.
            return true;
        }
    }

    private void ExitEcLock()
    {
        try
        {
            _ecMutex?.ReleaseMutex();
        }
        catch
        {
            // best effort
        }
    }

    // No sleep needed: each PioRead is a full kernel round-trip, which paces
    // the ~200-try poll budget on its own.
    private bool WaitFlag(int mask, bool set)
    {
        for (int i = 0; i < 200; i++)
        {
            int status = PioRead(EcStatusCommand);
            if (((status & mask) != 0) == set)
                return true;
        }
        return false;
    }

    private int PioRead(int port)
    {
        RequireEcPort(port);
        return Pio("ioctl_pio_read", new long[] { port }, outCount: 1) & 0xFF;
    }

    // Handshake bytes only (command/offset); never an EC RAM write.
    private void PioWrite(int port, int value)
    {
        RequireEcPort(port);
        Pio("ioctl_pio_write", new long[] { port, value }, outCount: 0);
    }

    private static void RequireEcPort(int port)
    {
        if (port is not (EcData or EcStatusCommand))
            throw new ArgumentOutOfRangeException(nameof(port), port, "Only EC ports 0x62 (data) and 0x66 (status/command) are allowed.");
    }

    // Returns the first output cell (0 for functions with no outputs).
    private int Pio(string cmd, long[] input, int outCount)
    {
        var output = new long[Math.Max(outCount, 1)];
        int hr = PawnIoNative.pawnio_execute(
            _handle, cmd, input, (UIntPtr)(uint)input.Length, output, (UIntPtr)(uint)outCount, out _);
        if (hr != 0)
            throw new EcUnavailableException($"pawnio_execute('{cmd}') failed with HRESULT 0x{hr:X8}.");
        return outCount > 0 ? (int)output[0] : 0;
    }

    private static string ResolveModulePath(string? modulePath)
    {
        if (modulePath is not null)
        {
            if (File.Exists(modulePath))
                return modulePath;
            throw new EcUnavailableException($"PawnIO module not found at '{modulePath}'.");
        }

        string[] candidates =
        {
            // next to the exe (deployment layout)
            Path.Combine(AppContext.BaseDirectory, "LpcACPIEC.bin"),
            // repo dev layout: the exe sits six levels below the root
            // (src/<project>/bin/x64/<config>/<tfm>/), the module in lib\pawnio\
            Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "lib", "pawnio", "LpcACPIEC.bin")),
            // a PawnIO install that ships its modules
            @"C:\Program Files\PawnIO\modules\LpcACPIEC.bin",
        };

        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        throw new EcUnavailableException(
            "PawnIO EC module LpcACPIEC.bin not found. Searched: " + string.Join("; ", candidates) +
            ". Place LpcACPIEC.bin next to the executable.");
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        // The lock is per-transaction, so nothing is held; just drop the handle.
        _ecMutex?.Dispose();
        _ecMutex = null;

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
