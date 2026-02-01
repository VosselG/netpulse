using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using NetPulse.Infrastructure;

namespace NetPulse.Services;

public sealed class VendorLookupService : IVendorLookupService
{
    private readonly object _gate = new();

    // Key: prefixHex (no separators, uppercase) e.g. "00000C" or "B827EB"
    // Value: vendor string
    private Dictionary<string, string>? _prefixToVendor;

    // Prefix lengths (hex chars) we support, searched in descending specificity.
    // Wireshark manuf commonly contains /36 (9 hex), /28 (7 hex), /24 (6 hex).
    private static readonly int[] SupportedPrefixHexLengthsDescending = [9, 7, 6];

    public string? TryGetVendor(string macAddress)
    {
        if (string.IsNullOrWhiteSpace(macAddress))
            return null;

        EnsureLoaded();

        var macHex = NormalizeMacToHex(macAddress);
        if (macHex is null || macHex.Length < 6)
            return null;

        var map = _prefixToVendor!;
        foreach (var prefixLen in SupportedPrefixHexLengthsDescending)
        {
            if (macHex.Length < prefixLen)
                continue;

            var prefix = macHex[..prefixLen];
            if (map.TryGetValue(prefix, out var vendor))
                return vendor;
        }

        return null;
    }

    private void EnsureLoaded()
    {
        if (_prefixToVendor is not null)
            return;

        lock (_gate)
        {
            if (_prefixToVendor is not null)
                return;

            _prefixToVendor = LoadManufFile();
        }
    }

    private static Dictionary<string, string> LoadManufFile()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Portable mode: file is copied alongside exe under Resources/manuf
        var path = Path.Combine(AppPaths.BaseDirectory, "Resources", "manuf");
        if (!File.Exists(path))
            return map;

        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                continue;

            // Wireshark manuf format is whitespace-separated:
            // <prefix> <shortName> <longName...>
            // Example:
            // 00:00:0C        Cisco       Cisco Systems, Inc
            //
            // We take "longName..." if present, otherwise shortName.
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                continue;

            var prefixToken = parts[0];
            var vendor = parts.Length >= 3
                ? string.Join(" ", parts.Skip(2))
                : parts[1];

            var (prefixHex, prefixHexLen) = TryParsePrefix(prefixToken);
            if (prefixHex is null)
                continue;

            // We only store prefixes we can match cheaply (/24, /28, /36 in hex-char terms).
            if (!SupportedPrefixHexLengthsDescending.Contains(prefixHexLen))
                continue;

            // First win is fine; manuf shouldn't have conflicting entries for the same prefix.
            map.TryAdd(prefixHex, vendor);
        }

        return map;
    }

    private static (string? PrefixHex, int PrefixHexLen) TryParsePrefix(string prefixToken)
    {
        // Token can be:
        // - "AA:BB:CC"
        // - "AA:BB:CC:DD:EE/36"
        // - "AABBCC"
        // - "AABBCC/24"
        var bits = 24;
        var raw = prefixToken;

        var slashIdx = prefixToken.IndexOf('/');
        if (slashIdx >= 0)
        {
            raw = prefixToken[..slashIdx];

            var bitsStr = prefixToken[(slashIdx + 1)..];
            if (!int.TryParse(bitsStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out bits))
                return (null, 0);
        }

        // We support only bit-lengths that align to hex nibbles to keep matching simple.
        if (bits <= 0 || bits % 4 != 0)
            return (null, 0);

        var hexLen = bits / 4;
        var hex = NormalizeMacToHex(raw);
        if (hex is null || hex.Length < hexLen)
            return (null, 0);

        return (hex[..hexLen], hexLen);
    }

    private static string? NormalizeMacToHex(string mac)
    {
        // Remove common separators and uppercase.
        var chars = new char[mac.Length];
        var count = 0;

        foreach (var c in mac)
        {
            if (c is ':' or '-' or '.')
                continue;

            chars[count++] = c;
        }

        if (count == 0)
            return null;

        var s = new string(chars, 0, count).ToUpperInvariant();

        // Validate: must be hex
        for (var i = 0; i < s.Length; i++)
        {
            var ch = s[i];
            var isHex =
                (ch >= '0' && ch <= '9') ||
                (ch >= 'A' && ch <= 'F');

            if (!isHex)
                return null;
        }

        return s;
    }
}