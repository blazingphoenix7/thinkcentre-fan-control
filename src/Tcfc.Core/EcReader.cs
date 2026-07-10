namespace Tcfc.Core;

/// <summary>
/// Read-only access to the physical embedded controller via the signed PawnIO
/// driver and its LpcACPIEC port-I/O module. Implements the standard ACPI EC
/// read handshake (RD_EC) on the 0x62/0x66 port pair, exactly as proven by the
/// elevated probe in <c>work/ec-probe.ps1</c>.
/// This type deliberately has no EC write capability: the app never writes EC
/// RAM through this path.
/// </summary>
public sealed class EcReader : IDisposable
{
    // ACPI embedded controller interface (fixed by the ACPI spec).
    private const int EcData = 0x62;          // data port
    private const int EcStatusCommand = 0x66; // status (read) / command (write) port
    private const int Obf = 0x01;             // status: output buffer full — a byte is ready on 0x62
    private const int Ibf = 0x02;             // status: input buffer full — EC has not consumed our last write
    private const int RdEc = 0x80;            // command: read EC RAM byte

    // System-wide EC lock shared with firmware/other EC users (best effort).
    // Taken per read transaction, never held across calls: holding it for the
    // object's lifetime would starve every other EC consumer on the machine
    // (including a second instance of this app's own CLI).
    private const string EcMutexName = @"Global\Access_EC";
    private const int EcMutexTimeoutMs = 500;

    // EC map for the verified target board: fan tach at 0x00 (hi) / 0x01 (lo),
    // temperature block at 0x21..0x2F.
    private const int RpmHighOffset = 0x00;
    private const int RpmLowOffset = 0x01;
    private const int TempFirstOffset = 0x21;
    private const int TempLastOffset = 0x2F;

    private IntPtr _handle;
    private Mutex? _ecMutex;
    private bool _disposed;

    /// <summary>
    /// Opens PawnIO and loads the LpcACPIEC module blob. When
    /// <paramref name="modulePath"/> is null the blob is searched next to the
    /// executable, then at the repository dev path relative to the build
    /// output, then in the PawnIO install's modules directory.
    /// </summary>
    /// <exception cref="EcUnavailableException">
    /// PawnIO is not installed / not reachable, the module blob is missing,
    /// or the driver rejected the open/load call (e.g. not elevated).
    /// </exception>
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
                @"PawnIOLib.dll could not be loaded — is PawnIO installed (expected under C:\Program Files\PawnIO)?", ex);
        }

        if (hr != 0)
        {
            _handle = IntPtr.Zero;
            throw new EcUnavailableException(
                $"pawnio_open failed with HRESULT 0x{hr:X8} — is the process elevated and the PawnIO driver running?");
        }

        hr = PawnIoNative.pawnio_load(_handle, blob, (UIntPtr)(uint)blob.Length);
        if (hr != 0)
        {
            PawnIoNative.pawnio_close(_handle);
            _handle = IntPtr.Zero;
            throw new EcUnavailableException($"pawnio_load of '{path}' failed with HRESULT 0x{hr:X8}.");
        }

        // Open (or create) the shared EC lock object. It is NOT acquired
        // here: each public read call takes it just for its own transaction.
        // Best effort — proceed without one if it cannot be opened or created.
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

    /// <summary>
    /// Reads one byte of EC RAM at <paramref name="offset"/> using the RD_EC
    /// handshake, under the shared EC lock. Returns -1 if the lock could not
    /// be acquired or the EC did not respond within the poll budget.
    /// </summary>
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

    /// <summary>
    /// Live fan RPM decoded from the tach register pair (0x00 high, 0x01 low),
    /// both bytes read under one hold of the shared EC lock. Returns -1 when
    /// the reading is unavailable (lock or EC timeout) — never a value
    /// fabricated from partial bytes.
    /// </summary>
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

    /// <summary>
    /// Raw bytes of the EC temperature block, offsets 0x21..0x2F inclusive
    /// (15 values), read under one hold of the shared EC lock; a value of -1
    /// means that offset (or the lock) timed out.
    /// </summary>
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

    /// <summary>
    /// The raw RD_EC handshake for one byte. Callers must hold the shared EC
    /// lock (see <see cref="TryEnterEcLock"/>). Returns -1 if the EC did not
    /// respond within the poll budget.
    /// </summary>
    private int ReadByteUnlocked(int offset)
    {
        // Drain any stale byte a previously timed-out transaction (ours or
        // another EC user's) left in the output buffer, so this read cannot
        // pick up a leftover value. Bounded: the EC only ever has one byte
        // pending, so a few iterations always clear it.
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

    /// <summary>
    /// Takes the shared EC lock for one read transaction. True means proceed
    /// (lock held, or no lock object exists on this system — then
    /// <see cref="ExitEcLock"/> is a no-op); false means another EC user held
    /// it past the timeout and the caller must report "unavailable" instead
    /// of touching the ports unlocked.
    /// </summary>
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
            // Previous holder died while owning the lock; ownership has
            // transferred to us and the handshake re-syncs the EC state.
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
            // Release is best effort; the paired TryEnterEcLock succeeded, so
            // this only guards against exotic handle states.
        }
    }

    /// <summary>
    /// Polls the EC status port until the <paramref name="mask"/> bit matches
    /// the wanted state, giving up after ~200 reads (each read is a full
    /// kernel round-trip, which is the pacing).
    /// </summary>
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

    /// <summary>Reads one byte from an EC port via the module's ioctl_pio_read.</summary>
    private int PioRead(int port)
    {
        RequireEcPort(port);
        return Pio("ioctl_pio_read", new long[] { port }, outCount: 1) & 0xFF;
    }

    /// <summary>
    /// Writes one byte to an EC port via the module's ioctl_pio_write. This is
    /// handshake traffic only (command/offset bytes) — never an EC RAM write.
    /// </summary>
    private void PioWrite(int port, int value)
    {
        RequireEcPort(port);
        Pio("ioctl_pio_write", new long[] { port, value }, outCount: 0);
    }

    /// <summary>Only the ACPI EC port pair may ever be touched.</summary>
    private static void RequireEcPort(int port)
    {
        if (port is not (EcData or EcStatusCommand))
            throw new ArgumentOutOfRangeException(nameof(port), port, "Only EC ports 0x62 (data) and 0x66 (status/command) are allowed.");
    }

    /// <summary>
    /// Runs a module function via pawnio_execute and returns the first output
    /// cell (0 for functions with no outputs). Throws if the driver call fails.
    /// </summary>
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
            // Next to the executable — the deployment layout.
            Path.Combine(AppContext.BaseDirectory, "LpcACPIEC.bin"),
            // Repo dev layout: the exe sits six directories below the repo
            // root (src/<project>/bin/x64/<config>/<tfm>/), the module in
            // lib\pawnio\ at the root.
            Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "lib", "pawnio", "LpcACPIEC.bin")),
            // A PawnIO install that ships its modules.
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

        // The lock is taken per read transaction, so there is nothing to
        // release here — just drop the handle.
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
