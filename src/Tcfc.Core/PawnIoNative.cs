using System.Runtime.InteropServices;

namespace Tcfc.Core;

/// <summary>
/// P/Invoke surface of PawnIOLib.dll (signatures match PawnIOLib.h; every
/// function returns an HRESULT where 0 = success). The library ships in the
/// PawnIO install directory, which is not on the default DLL search path, so
/// the static constructor registers it before the first call resolves.
/// </summary>
internal static class PawnIoNative
{
    private const string PawnIoInstallDirectory = @"C:\Program Files\PawnIO";

    static PawnIoNative()
    {
        // Best effort; if PawnIO lives elsewhere (e.g. System32) the normal
        // search path still applies. The return value is deliberately ignored.
        SetDllDirectory(PawnIoInstallDirectory);
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool SetDllDirectory(string p);

    [DllImport("PawnIOLib.dll")]
    internal static extern int pawnio_open(out IntPtr h);

    [DllImport("PawnIOLib.dll")]
    internal static extern int pawnio_load(IntPtr h, byte[] blob, UIntPtr size);

    [DllImport("PawnIOLib.dll", CharSet = CharSet.Ansi)]
    internal static extern int pawnio_execute(
        IntPtr h, string name, long[] inArr, UIntPtr inN, long[] outArr, UIntPtr outN, out UIntPtr retN);

    [DllImport("PawnIOLib.dll")]
    internal static extern int pawnio_close(IntPtr h);
}
