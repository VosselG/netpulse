namespace NetPulse.Models;

public sealed class UserSettings
{
    public bool AutoPruneEnabled { get; set; } = false;

    public int AutoPruneDays { get; set; } = 3;
}