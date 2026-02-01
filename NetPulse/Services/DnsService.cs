using System.Net;

namespace NetPulse.Services;

public sealed class DnsService : IDnsService
{
    public async Task<string?> ReverseLookupAsync(IPAddress ipAddress, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            var entry = await Dns.GetHostEntryAsync(ipAddress)
                .WaitAsync(timeout, cancellationToken)
                .ConfigureAwait(false);

            if (entry is null)
                return null;

            return string.IsNullOrWhiteSpace(entry.HostName) ? null : entry.HostName;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TimeoutException)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }
}