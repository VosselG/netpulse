using System.IO;

namespace NetPulse.Infrastructure;

public static class AppPaths
{
    // Portable mode: store files alongside the executable.
    public static string BaseDirectory => AppContext.BaseDirectory;

    public static string DevicesJsonPath => Path.Combine(BaseDirectory, "devices.json");

    public static string SettingsJsonPath => Path.Combine(BaseDirectory, "settings.json");
}