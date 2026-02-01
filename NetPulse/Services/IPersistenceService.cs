using NetPulse.Models;

namespace NetPulse.Services;

public interface IPersistenceService
{
    Task<IReadOnlyList<NetworkDevice>> LoadDevicesAsync(CancellationToken cancellationToken);

    Task SaveDevicesAsync(IEnumerable<NetworkDevice> devices, CancellationToken cancellationToken);

    Task<UserSettings> LoadSettingsAsync(CancellationToken cancellationToken);

    Task SaveSettingsAsync(UserSettings settings, CancellationToken cancellationToken);
}