namespace NetPulse.Services;

public interface IVendorLookupService
{
    /// <summary>
    /// Returns a vendor/manufacturer name for the MAC address if found; otherwise null.
    /// </summary>
    string? TryGetVendor(string macAddress);
}