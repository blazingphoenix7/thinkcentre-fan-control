using System.Runtime.InteropServices;

namespace Tcfc.Core;

/// <summary>P/Invoke for PawnIOLib.dll (signatures per PawnIOLib.h; every function returns an HRESULT, 0 = success).</summary>
internal static class PawnIoNative
{
    private const string PawnIoInstallDirectory = @"C:\Program Files\PawnIO";

    static PawnIoNative()
    {
        // The PawnIO install dir is not on the default DLL search path.
        // Best effort; if it lives elsewhere the normal search still applies.
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
