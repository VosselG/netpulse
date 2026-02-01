using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using NetPulse.Infrastructure;
using NetPulse.Models;

namespace NetPulse.Services;

/// <summary>
/// Discovery scanner (ARP sweep).
/// Determines local subnet by active NIC IPv4 + subnet mask, then attempts ARP resolution for each host.
/// If ARP succeeds, the device is considered present/online. We also attempt Ping to capture latency (if allowed).
/// </summary>
public sealed class ScannerService : IScannerService
{
    // Ping is "best effort" here. Many devices block ICMP.
    private const int DefaultPingTimeoutMs = 1500;
    private const int DefaultPingRetries = 1;

    // Gentler parallelism to reduce network noise and CPU spikes.
    private const int DefaultMaxDegreeOfParallelism = 64;

    // ARP is synchronous; wrap each attempt and enforce a timeout.
    private static readonly TimeSpan ArpTimeout = TimeSpan.FromMilliseconds(800);

    // Guardrail to prevent accidental huge scans (/16, etc.).
    private const int MaxHostsToScan = 4096;

    public async Task<IReadOnlyList<NetworkDevice>> DiscoverAsync(
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var (localIp, mask) = GetPrimaryIPv4AndMask();

        var hosts = EnumerateHosts(localIp, mask).ToArray();
        if (hosts.Length == 0)
            return Array.Empty<NetworkDevice>();

        if (hosts.Length > MaxHostsToScan)
            throw new InvalidOperationException(
                $"Subnet has {hosts.Length} hosts; refusing to scan more than {MaxHostsToScan} in this version.");

        progress?.Report(new ScanProgress(0, hosts.Length));

        var results = new ConcurrentBag<NetworkDevice>();
        var completed = 0;

        await Parallel.ForEachAsync(
            hosts,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = DefaultMaxDegreeOfParallelism,
                CancellationToken = cancellationToken
            },
            async (ip, ct) =>
            {
                try
                {
                    // ARP sweep: if we can resolve a MAC for the IP, treat it as a present device.
                    var mac = await TryResolveMacWithTimeoutAsync(ip, ct).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(mac))
                    {
                        // Ping is best-effort for latency; device is still "online" if ARP succeeded.
                        var latencyMs = await TryPingLatencyMsAsync(
                                ip,
                                timeoutMs: DefaultPingTimeoutMs,
                                retries: DefaultPingRetries,
                                ct)
                            .ConfigureAwait(false);

                        results.Add(new NetworkDevice
                        {
                            MacAddress = mac,
                            IpAddress = ip.ToString(),
                            IsOnline = true,
                            LastSeen = DateTime.UtcNow,
                            LastLatencyMs = latencyMs
                        });
                    }
                }
                finally
                {
                    var done = Interlocked.Increment(ref completed);
                    progress?.Report(new ScanProgress(done, hosts.Length));
                }
            }).ConfigureAwait(false);

        // De-duplicate by MAC (one device identity), prefer entries that have latency (if any).
        var deduped = results
            .Where(d => !string.IsNullOrWhiteSpace(d.MacAddress))
            .GroupBy(d => d.MacAddress, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
                g.OrderByDescending(d => d.LastLatencyMs.HasValue) // prefer pingable
                 .ThenByDescending(d => d.LastSeen)
                 .First())
            .OrderBy(d => IPv4ToUInt32(IPAddress.Parse(d.IpAddress)))
            .ToList();

        return deduped;
    }

    private static async Task<string?> TryResolveMacWithTimeoutAsync(IPAddress ip, CancellationToken cancellationToken)
    {
        // SendARP is synchronous and can block; push it to the threadpool and enforce a timeout.
        var task = Task.Run(() => ArpInterop.TryResolveMacAddress(ip), cancellationToken);

        try
        {
            return await task.WaitAsync(ArpTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    private static async Task<int?> TryPingLatencyMsAsync(
        IPAddress ip,
        int timeoutMs,
        int retries,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt <= retries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(ip, timeoutMs)
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);

                if (reply.Status == IPStatus.Success)
                    return (int)reply.RoundtripTime;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // ignore and retry
            }
        }

        return null;
    }

    private static (IPAddress LocalIp, IPAddress Mask) GetPrimaryIPv4AndMask()
    {
        var candidates = NetworkInterface.GetAllNetworkInterfaces()
            .Where(ni =>
                ni.OperationalStatus == OperationalStatus.Up &&
                ni.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                ni.NetworkInterfaceType != NetworkInterfaceType.Tunnel);

        // Prefer interfaces with a default gateway, then any usable IPv4 interface.
        var best = candidates
            .OrderByDescending(HasDefaultGateway)
            .FirstOrDefault(HasUsableIPv4);

        if (best is null)
            throw new InvalidOperationException("No active IPv4 network interface found.");

        var ipProps = best.GetIPProperties();

        var uni = ipProps.UnicastAddresses.FirstOrDefault(u =>
            u.Address.AddressFamily == AddressFamily.InterNetwork &&
            !IPAddress.IsLoopback(u.Address) &&
            !IsApipa(u.Address));

        if (uni?.IPv4Mask is null)
            throw new InvalidOperationException("Could not determine IPv4 subnet mask for the active interface.");

        return (uni.Address, uni.IPv4Mask);
    }

    private static bool HasUsableIPv4(NetworkInterface ni)
    {
        try
        {
            var ipProps = ni.GetIPProperties();
            return ipProps.UnicastAddresses.Any(u =>
                u.Address.AddressFamily == AddressFamily.InterNetwork &&
                !IPAddress.IsLoopback(u.Address) &&
                !IsApipa(u.Address) &&
                u.IPv4Mask is not null);
        }
        catch
        {
            return false;
        }
    }

    private static bool HasDefaultGateway(NetworkInterface ni)
    {
        try
        {
            var ipProps = ni.GetIPProperties();
            return ipProps.GatewayAddresses.Any(g =>
                g.Address is not null &&
                g.Address.AddressFamily == AddressFamily.InterNetwork &&
                !g.Address.Equals(IPAddress.Any) &&
                !g.Address.Equals(IPAddress.None));
        }
        catch
        {
            return false;
        }
    }

    private static bool IsApipa(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254;
    }

    private static IEnumerable<IPAddress> EnumerateHosts(IPAddress localIp, IPAddress mask)
    {
        var ip = IPv4ToUInt32(localIp);
        var m = IPv4ToUInt32(mask);

        var network = ip & m;
        var broadcast = network | ~m;

        // /32 or unexpected -> just scan local IP
        if (broadcast <= network)
        {
            yield return localIp;
            yield break;
        }

        var start = network + 1;
        var end = broadcast - 1;

        if (end < start)
        {
            // /31: scan both endpoints
            yield return UInt32ToIPv4(network);
            yield return UInt32ToIPv4(broadcast);
            yield break;
        }

        for (uint cur = start; cur <= end; cur++)
            yield return UInt32ToIPv4(cur);
    }

    private static uint IPv4ToUInt32(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        if (bytes.Length != 4)
            throw new ArgumentException("Address is not IPv4.", nameof(address));

        return ((uint)bytes[0] << 24)
             | ((uint)bytes[1] << 16)
             | ((uint)bytes[2] << 8)
             | bytes[3];
    }

    private static IPAddress UInt32ToIPv4(uint value)
    {
        var bytes = new byte[]
        {
            (byte)((value >> 24) & 0xFF),
            (byte)((value >> 16) & 0xFF),
            (byte)((value >> 8) & 0xFF),
            (byte)(value & 0xFF)
        };

        return new IPAddress(bytes);
    }
}