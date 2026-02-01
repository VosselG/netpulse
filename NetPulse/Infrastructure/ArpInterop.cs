using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace NetPulse.Infrastructure;

internal static class ArpInterop
{
    // https://learn.microsoft.com/windows/win32/api/iphlpapi/nf-iphlpapi-sendarp
    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int SendARP(int destIp, int srcIp, byte[] macAddr, ref int phyAddrLen);

    /// <summary>
    /// Attempts to resolve a MAC address for an IPv4 address using an ARP request.
    /// Returns MAC formatted as "AA:BB:CC:DD:EE:FF" or null if not resolved.
    /// </summary>
    public static string? TryResolveMacAddress(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork)
            return null;

        var mac = new byte[6];
        var len = mac.Length;

        // SendARP expects the destination IP as an int; common practice is to pass the raw bytes via BitConverter.
        var destIp = BitConverter.ToInt32(ip.GetAddressBytes(), 0);

        var result = SendARP(destIp, 0, mac, ref len);
        if (result != 0 || len <= 0)
            return null;

        if (len > mac.Length)
            len = mac.Length;

        // Treat all-zero MAC as invalid.
        var allZero = true;
        for (var i = 0; i < len; i++)
        {
            if (mac[i] != 0)
            {
                allZero = false;
                break;
            }
        }

        if (allZero)
            return null;

        return string.Join(":", mac.Take(len).Select(b => b.ToString("X2")));
    }
}