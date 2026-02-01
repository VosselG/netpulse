using System.IO;
using System.Text.Json;
using NetPulse.Infrastructure;
using NetPulse.Models;

namespace NetPulse.Services;

public sealed class PersistenceService : IPersistenceService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public async Task<IReadOnlyList<NetworkDevice>> LoadDevicesAsync(CancellationToken cancellationToken)
    {
        var path = AppPaths.DevicesJsonPath;

        if (!File.Exists(path))
            return Array.Empty<NetworkDevice>();

        string json;
        try
        {
            json = await File.ReadAllTextAsync(path, cancellationToken);
        }
        catch
        {
            // If the file is temporarily locked/unreadable, fail "soft" at scaffolding stage.
            return Array.Empty<NetworkDevice>();
        }

        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<NetworkDevice>();

        try
        {
            return JsonSerializer.Deserialize<List<NetworkDevice>>(json, JsonOptions) ?? new List<NetworkDevice>();
        }
        catch
        {
            // If JSON is corrupted, fail "soft" (future: surface error to UI).
            return Array.Empty<NetworkDevice>();
        }
    }

    public Task SaveDevicesAsync(IEnumerable<NetworkDevice> devices, CancellationToken cancellationToken)
        => SaveJsonAtomicAsync(AppPaths.DevicesJsonPath, devices, cancellationToken);

    public async Task<UserSettings> LoadSettingsAsync(CancellationToken cancellationToken)
    {
        var path = AppPaths.SettingsJsonPath;

        if (!File.Exists(path))
            return new UserSettings();

        string json;
        try
        {
            json = await File.ReadAllTextAsync(path, cancellationToken);
        }
        catch
        {
            return new UserSettings();
        }

        if (string.IsNullOrWhiteSpace(json))
            return new UserSettings();

        try
        {
            return JsonSerializer.Deserialize<UserSettings>(json, JsonOptions) ?? new UserSettings();
        }
        catch
        {
            return new UserSettings();
        }
    }

    public Task SaveSettingsAsync(UserSettings settings, CancellationToken cancellationToken)
        => SaveJsonAtomicAsync(AppPaths.SettingsJsonPath, settings, cancellationToken);

    private static async Task SaveJsonAtomicAsync<T>(string destinationPath, T payload, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var tmpPath = destinationPath + ".tmp";

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        await File.WriteAllTextAsync(tmpPath, json, cancellationToken);

        // Replace destination atomically-ish (best effort on Windows).
        File.Move(tmpPath, destinationPath, overwrite: true);
    }
}