using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetPulse.Infrastructure;
using NetPulse.Models;
using NetPulse.Services;
using System.ComponentModel;

namespace NetPulse.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IPersistenceService _persistenceService;
    private readonly IScannerService _scannerService;

    private CancellationTokenSource? _monitoringCts;
    private Task? _monitoringTask;

    // Requested change: reduce flapping
    private const int OfflineAfterConsecutiveMisses = 10;

    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(2);

    private const int MonitoringMaxDegreeOfParallelism = 32;

    private static readonly TimeSpan ArpTimeout = TimeSpan.FromMilliseconds(800);
    private const int HeartbeatPingTimeoutMs = 1000;
    private const int HeartbeatPingRetries = 0;

    private const int PingHistoryMaxPoints = 60;

    public ObservableCollection<NetworkDevice> Devices { get; } = new();

    // Two filtered/sorted views for the UI.
    public ICollectionView OnlineDevicesView { get; }
    public ICollectionView OfflineDevicesView { get; }

    [ObservableProperty]
    private bool hasOnlineDevices;

    [ObservableProperty]
    private bool hasOfflineDevices;

    // IMPORTANT: Do not bind both ListBoxes directly to SelectedDevice.
    // They will fight and can clear selection because the selected item isn't in the other list.
    [ObservableProperty]
    private NetworkDevice? onlineSelectedDevice;

    [ObservableProperty]
    private NetworkDevice? offlineSelectedDevice;

    [ObservableProperty]
    private NetworkDevice? selectedDevice;

    [ObservableProperty]
    private UserSettings settings = new();

    [ObservableProperty]
    private bool isScanning;

    [ObservableProperty]
    private double scanProgressPercent;

    [ObservableProperty]
    private string scanStatusText = string.Empty;

    private bool _syncingSelection;

    public MainViewModel(IPersistenceService persistenceService, IScannerService scannerService)
    {
        _persistenceService = persistenceService;
        _scannerService = scannerService;

        OnlineDevicesView = new ListCollectionView(Devices)
        {
            Filter = o => o is NetworkDevice d && d.IsOnline
        };

        OfflineDevicesView = new ListCollectionView(Devices)
        {
            Filter = o => o is NetworkDevice d && !d.IsOnline
        };

        if (OnlineDevicesView is ListCollectionView onlineLcv)
            onlineLcv.CustomSort = new DeviceIpComparer();

        if (OfflineDevicesView is ListCollectionView offlineLcv)
            offlineLcv.CustomSort = new DeviceIpComparer();
    }

    partial void OnOnlineSelectedDeviceChanged(NetworkDevice? value)
    {
        if (_syncingSelection)
            return;

        _syncingSelection = true;
        try
        {
            if (value is not null)
            {
                SelectedDevice = value;
                if (OfflineSelectedDevice is not null)
                    OfflineSelectedDevice = null;
            }
            else
            {
                // If the online selection cleared and there is no offline selection, clear SelectedDevice.
                if (OfflineSelectedDevice is null && SelectedDevice is not null && SelectedDevice.IsOnline)
                    SelectedDevice = null;
            }
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    partial void OnOfflineSelectedDeviceChanged(NetworkDevice? value)
    {
        if (_syncingSelection)
            return;

        _syncingSelection = true;
        try
        {
            if (value is not null)
            {
                SelectedDevice = value;
                if (OnlineSelectedDevice is not null)
                    OnlineSelectedDevice = null;
            }
            else
            {
                // If the offline selection cleared and there is no online selection, clear SelectedDevice.
                if (OnlineSelectedDevice is null && SelectedDevice is not null && !SelectedDevice.IsOnline)
                    SelectedDevice = null;
            }
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    partial void OnSelectedDeviceChanged(NetworkDevice? value)
    {
        if (_syncingSelection)
            return;

        _syncingSelection = true;
        try
        {
            if (value is null)
            {
                OnlineSelectedDevice = null;
                OfflineSelectedDevice = null;
                return;
            }

            if (value.IsOnline)
            {
                OnlineSelectedDevice = value;
                OfflineSelectedDevice = null;
            }
            else
            {
                OfflineSelectedDevice = value;
                OnlineSelectedDevice = null;
            }
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    private sealed class DeviceIpComparer : System.Collections.IComparer
    {
        public int Compare(object? x, object? y)
        {
            var a = x as NetworkDevice;
            var b = y as NetworkDevice;
            if (a is null || b is null)
                return 0;

            var au = TryParseIPv4ToUInt32(a.IpAddress);
            var bu = TryParseIPv4ToUInt32(b.IpAddress);

            if (au.HasValue && bu.HasValue)
                return au.Value.CompareTo(bu.Value);

            return string.Compare(a.IpAddress, b.IpAddress, StringComparison.OrdinalIgnoreCase);
        }

        private static uint? TryParseIPv4ToUInt32(string? ipString)
        {
            if (string.IsNullOrWhiteSpace(ipString))
                return null;

            if (!IPAddress.TryParse(ipString.Trim(), out var ip))
                return null;

            var bytes = ip.GetAddressBytes();
            if (bytes.Length != 4)
                return null;

            return ((uint)bytes[0] << 24)
                 | ((uint)bytes[1] << 16)
                 | ((uint)bytes[2] << 8)
                 | bytes[3];
        }
    }

    private void RefreshViewsAndSectionState()
    {
        OnlineDevicesView.Refresh();
        OfflineDevicesView.Refresh();

        HasOnlineDevices = Devices.Any(d => d.IsOnline);
        HasOfflineDevices = Devices.Any(d => !d.IsOnline);

        // If a selected device changed online/offline state, keep selection highlight in the right section.
        if (SelectedDevice is not null)
        {
            _syncingSelection = true;
            try
            {
                if (SelectedDevice.IsOnline)
                {
                    OnlineSelectedDevice = SelectedDevice;
                    OfflineSelectedDevice = null;
                }
                else
                {
                    OfflineSelectedDevice = SelectedDevice;
                    OnlineSelectedDevice = null;
                }
            }
            finally
            {
                _syncingSelection = false;
            }
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        Settings = await _persistenceService.LoadSettingsAsync(cancellationToken);

        var devices = await _persistenceService.LoadDevicesAsync(cancellationToken);
        Devices.Clear();

        foreach (var d in devices)
        {
            // Monitoring will take over; start offline at launch until first heartbeat confirms presence.
            d.IsOnline = false;
            d.LastLatencyMs = null;
            d.ConsecutiveMisses = 0;
            Devices.Add(d);
        }

        ScanProgressPercent = 0;
        ScanStatusText = string.Empty;

        RefreshViewsAndSectionState();
        StartMonitoring();
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        await StopMonitoringAsync(cancellationToken);

        // Persist only what should be persisted (transient properties are [JsonIgnore]).
        await _persistenceService.SaveDevicesAsync(Devices, cancellationToken);
        await _persistenceService.SaveSettingsAsync(Settings, cancellationToken);
    }

    private void StartMonitoring()
    {
        if (_monitoringTask is not null)
            return;

        _monitoringCts = new CancellationTokenSource();
        var ct = _monitoringCts.Token;

        _monitoringTask = Task.Run(() => MonitoringLoopAsync(ct), ct);
    }

    private async Task StopMonitoringAsync(CancellationToken cancellationToken)
    {
        if (_monitoringCts is null || _monitoringTask is null)
            return;

        try
        {
            _monitoringCts.Cancel();

            // Best effort: don't hang shutdown forever.
            var completed = await Task.WhenAny(_monitoringTask, Task.Delay(TimeSpan.FromSeconds(2), cancellationToken));
            _ = completed; // intentionally ignored
        }
        catch
        {
            // best effort shutdown
        }
        finally
        {
            _monitoringCts.Dispose();
            _monitoringCts = null;
            _monitoringTask = null;
        }
    }

    private sealed record HeartbeatUpdate(
        NetworkDevice Device,
        bool IsPresent,
        string? ResolvedMac,
        int? LatencyMs,
        DateTime TimestampUtc);

    private async Task MonitoringLoopAsync(CancellationToken cancellationToken)
    {
        var timer = new PeriodicTimer(HeartbeatInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                // Avoid fighting with discovery scan.
                if (IsScanning)
                    continue;

                NetworkDevice[] snapshot;
                try
                {
                    // Snapshot the ObservableCollection on the UI thread.
                    snapshot = await Application.Current.Dispatcher.InvokeAsync(() => Devices.ToArray());
                }
                catch
                {
                    // If dispatcher/app is shutting down, exit the loop.
                    break;
                }

                if (snapshot.Length == 0)
                    continue;

                var timestampUtc = DateTime.UtcNow;
                var updates = new ConcurrentBag<HeartbeatUpdate>();

                await Parallel.ForEachAsync(
                    snapshot,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = MonitoringMaxDegreeOfParallelism,
                        CancellationToken = cancellationToken
                    },
                    async (device, ct) =>
                    {
                        try
                        {
                            if (string.IsNullOrWhiteSpace(device.IpAddress) ||
                                !IPAddress.TryParse(device.IpAddress.Trim(), out var ip))
                            {
                                updates.Add(new HeartbeatUpdate(device, IsPresent: false, ResolvedMac: null, LatencyMs: null, timestampUtc));
                                return;
                            }

                            // Online definition: online if ARP succeeds (identity-aware via MAC).
                            var resolvedMac = await TryResolveMacWithTimeoutAsync(ip, ct).ConfigureAwait(false);

                            var macMatches =
                                !string.IsNullOrWhiteSpace(resolvedMac) &&
                                (string.IsNullOrWhiteSpace(device.MacAddress) ||
                                 string.Equals(device.MacAddress.Trim(), resolvedMac, StringComparison.OrdinalIgnoreCase));

                            if (!macMatches)
                            {
                                updates.Add(new HeartbeatUpdate(device, IsPresent: false, ResolvedMac: resolvedMac, LatencyMs: null, timestampUtc));
                                return;
                            }

                            // Present via ARP. Now attempt Ping for latency (best-effort).
                            var latencyMs = await TryPingLatencyMsAsync(ip, HeartbeatPingTimeoutMs, HeartbeatPingRetries, ct).ConfigureAwait(false);

                            updates.Add(new HeartbeatUpdate(device, IsPresent: true, ResolvedMac: resolvedMac, LatencyMs: latencyMs, timestampUtc));
                        }
                        catch (OperationCanceledException)
                        {
                            // expected on shutdown
                        }
                        catch
                        {
                            // Treat unexpected errors as a miss for this tick.
                            updates.Add(new HeartbeatUpdate(device, IsPresent: false, ResolvedMac: null, LatencyMs: null, timestampUtc));
                        }
                    }).ConfigureAwait(false);

                // Apply updates on UI thread to avoid cross-thread property notifications.
                try
                {
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        foreach (var u in updates)
                        {
                            if (!Devices.Contains(u.Device))
                                continue;

                            // Upgrade IP-only persisted entries to include MAC once we learn it.
                            if (!string.IsNullOrWhiteSpace(u.ResolvedMac) && string.IsNullOrWhiteSpace(u.Device.MacAddress))
                                u.Device.MacAddress = u.ResolvedMac;

                            if (u.IsPresent)
                            {
                                u.Device.ConsecutiveMisses = 0;
                                u.Device.IsOnline = true;
                                u.Device.LastSeen = u.TimestampUtc;

                                // Only show latency if Ping succeeded.
                                u.Device.LastLatencyMs = u.LatencyMs;

                                if (u.LatencyMs.HasValue)
                                {
                                    u.Device.PingHistory.Add(new PingPoint(u.TimestampUtc, u.LatencyMs.Value));
                                    while (u.Device.PingHistory.Count > PingHistoryMaxPoints)
                                        u.Device.PingHistory.RemoveAt(0);
                                }
                            }
                            else
                            {
                                u.Device.ConsecutiveMisses++;

                                if (u.Device.ConsecutiveMisses >= OfflineAfterConsecutiveMisses)
                                {
                                    u.Device.IsOnline = false;
                                    u.Device.LastLatencyMs = null;
                                }
                            }
                        }

                        RefreshViewsAndSectionState();
                    });
                }
                catch
                {
                    break;
                }
            }
        }
        finally
        {
            timer.Dispose();
        }
    }

    private static async Task<string?> TryResolveMacWithTimeoutAsync(IPAddress ip, CancellationToken cancellationToken)
    {
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

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (IsScanning)
            return;

        IsScanning = true;
        ScanProgressPercent = 0;
        ScanStatusText = "Scanning...";

        // Let WPF render the updated UI state before starting the heavy scan work.
        await Task.Yield();

        try
        {
            // For discovery, mark all known devices offline first; scan will mark hits online.
            foreach (var d in Devices)
                d.IsOnline = false;

            RefreshViewsAndSectionState();

            var progress = new Progress<ScanProgress>(p =>
            {
                if (p.Total <= 0)
                {
                    ScanProgressPercent = 0;
                    ScanStatusText = "Scanning...";
                    return;
                }

                var pct = (double)p.Completed * 100.0 / p.Total;
                if (pct < 0) pct = 0;
                if (pct > 100) pct = 100;

                ScanProgressPercent = pct;
                ScanStatusText = $"Scanning {p.Completed}/{p.Total}";
            });

            var results = await _scannerService.DiscoverAsync(progress, CancellationToken.None);

            // Primary identity is MAC. We keep an "upgrade path" for older persisted entries
            // that were saved without a MAC (IP-only milestone).
            var byMac = Devices
                .Where(d => !string.IsNullOrWhiteSpace(d.MacAddress))
                .ToDictionary(d => d.MacAddress.Trim(), d => d, StringComparer.OrdinalIgnoreCase);

            var ipOnlyByIp = Devices
                .Where(d => string.IsNullOrWhiteSpace(d.MacAddress) && !string.IsNullOrWhiteSpace(d.IpAddress))
                .GroupBy(d => d.IpAddress.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            foreach (var found in results)
            {
                if (string.IsNullOrWhiteSpace(found.MacAddress))
                    continue;

                var macKey = found.MacAddress.Trim();

                if (!byMac.TryGetValue(macKey, out var existing))
                {
                    // Upgrade an existing IP-only entry to MAC-based identity if IP matches.
                    if (!string.IsNullOrWhiteSpace(found.IpAddress) &&
                        ipOnlyByIp.TryGetValue(found.IpAddress.Trim(), out var ipOnlyExisting))
                    {
                        ipOnlyExisting.MacAddress = macKey;
                        existing = ipOnlyExisting;

                        byMac[macKey] = existing;
                        ipOnlyByIp.Remove(found.IpAddress.Trim());
                    }
                    else
                    {
                        found.IsOnline = true;
                        found.LastSeen = found.LastSeen == DateTime.MinValue ? DateTime.UtcNow : found.LastSeen;

                        Devices.Add(found);
                        byMac[macKey] = found;
                        continue;
                    }
                }

                if (!string.IsNullOrWhiteSpace(found.IpAddress))
                    existing.IpAddress = found.IpAddress;

                if (!string.IsNullOrWhiteSpace(found.Hostname))
                    existing.Hostname = found.Hostname;

                if (!string.IsNullOrWhiteSpace(found.Vendor))
                    existing.Vendor = found.Vendor;

                existing.LastLatencyMs = found.LastLatencyMs;
                existing.LastSeen = found.LastSeen == DateTime.MinValue ? DateTime.UtcNow : found.LastSeen;
                existing.IsOnline = true;
                existing.ConsecutiveMisses = 0;
            }

            RefreshViewsAndSectionState();

            ScanProgressPercent = 100;
            ScanStatusText = "Scan complete";
        }
        catch (Exception ex)
        {
            ScanStatusText = $"Scan failed: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
        }
    }

    [RelayCommand]
    private void DeleteSelectedDevice()
    {
        if (SelectedDevice is null)
            return;

        Devices.Remove(SelectedDevice);
        SelectedDevice = null;

        RefreshViewsAndSectionState();
    }
}