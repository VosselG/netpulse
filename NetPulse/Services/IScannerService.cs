using NetPulse.Models;

namespace NetPulse.Services;

public interface IScannerService
{
    Task<IReadOnlyList<NetworkDevice>> DiscoverAsync(
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken);
}

public readonly record struct ScanProgress(int Completed, int Total);