using System.Reflection;

namespace Tcfc.Tray;

// The app icon (app.ico), embedded in the assembly so the tray icon and the
// window can load it at runtime, alongside it being the exe's own Win32 icon.
// Returns null if the resource is somehow missing, so callers can fall back.
internal static class AppIcon
{
    public static Icon? Load()
    {
        try
        {
            Assembly asm = Assembly.GetExecutingAssembly();
            string? name = Array.Find(
                asm.GetManifestResourceNames(),
                n => n.EndsWith("app.ico", StringComparison.OrdinalIgnoreCase));
            if (name is null)
                return null;
            using Stream? stream = asm.GetManifestResourceStream(name);
            return stream is null ? null : new Icon(stream);
        }
        catch
        {
            return null;
        }
    }
}
