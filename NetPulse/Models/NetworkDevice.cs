using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace NetPulse.Models;

public partial class NetworkDevice : ObservableObject
{
    // Persisted properties (saved to devices.json)
    [ObservableProperty]
    private string macAddress = string.Empty;

    [ObservableProperty]
    private string ipAddress = string.Empty;

    [ObservableProperty]
    private string vendor = string.Empty;

    [ObservableProperty]
    private string hostname = string.Empty;

    [ObservableProperty]
    private DateTime lastSeen = DateTime.MinValue;

    // Transient properties (runtime only)
    [JsonIgnore]
    [ObservableProperty]
    private bool isOnline;

    [JsonIgnore]
    [ObservableProperty]
    private int? lastLatencyMs;

    [JsonIgnore]
    public int ConsecutiveMisses { get; set; } = 0;

    [JsonIgnore]
    public DateTime LastHostnameResolveAttemptUtc { get; set; } = DateTime.MinValue;

    [JsonIgnore]
    public ObservableCollection<PingPoint> PingHistory { get; } = new();

    [JsonIgnore]
    public string DisplayName =>
        !string.IsNullOrWhiteSpace(Hostname) ? Hostname :
        !string.IsNullOrWhiteSpace(IpAddress) ? IpAddress :
        "Unknown device";

    partial void OnHostnameChanged(string value) => OnPropertyChanged(nameof(DisplayName));

    partial void OnIpAddressChanged(string value) => OnPropertyChanged(nameof(DisplayName));
}