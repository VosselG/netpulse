using System.Net;

namespace NetPulse.Services;

public interface IDnsService
{
    Task<string?> ReverseLookupAsync(IPAddress ipAddress, TimeSpan timeout, CancellationToken cancellationToken);
}