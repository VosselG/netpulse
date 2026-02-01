# NetPulse

NetPulse is a Windows desktop app (WPF, .NET 8) for discovering devices on your local network and monitoring their connectivity in real time. It provides a dark-mode dashboard with device cards, a detail sidebar, and a live latency graph.

> **Status:** actively built/iterated. Discovery + monitoring + persistence are working.

---

## Features

### Discovery (Active Scan)
- Manual **Scan** button
- Scans your local subnet (derived from your active network interface IPv4 + subnet mask)
- Uses **ARP-based discovery** (plus an ICMP ping attempt for latency)
- Shows deterministic scan progress

### Monitoring (Heartbeat)
- Automatically runs in the background
- Updates each known device:
  - Online/offline state
  - Last seen timestamp
  - Latency (when ping succeeds)
  - Ping history (for the graph)
- Uses a “miss threshold” to avoid flapping (device goes offline after several missed checks)

### Device Identity & Enrichment
- Devices are uniquely tracked by **MAC address** (handles DHCP IP changes)
- Reverse DNS hostname resolution (best-effort)
- Vendor lookup from a local Wireshark-style `manuf` OUI database (best-effort)

### UI/UX
- Modern dark mode dashboard
- Wrap-style device cards
- Online devices listed above offline devices
- Green status indicator for online
- Detail sidebar with a live latency graph
- Settings panel for auto-prune

### Persistence
- Saves and restores device list and settings:
  - `devices.json`
  - `settings.json`
- Uses “portable mode” storage (stored next to the executable / output)

### Auto-Prune
- Optional automatic cleanup of stale offline devices
- Runs on startup and daily when enabled
- Manual **Prune now** button

---

## Requirements
- Windows 10/11
- .NET 8 SDK (for development)

---

## Run (Dev)

From the repo root:

```bash
cd NetPulse
dotnet run
```

This will build and run the WPF app.

---

## Where data is stored (Portable Mode)

NetPulse stores its files next to the executable/output directory:

- `devices.json`
- `settings.json`
- `Resources/manuf` (vendor/OUI database; copied to output)

When running via `dotnet run`, these will appear in the build output folder (e.g. `bin/Debug/net8.0-windows/...`).

---

## How it works (high-level)

### 1) Discovery: “Who is on my LAN?”
NetPulse determines your active network interface’s IPv4 address and subnet mask, computes the host IP range, then scans each IP.

To find devices reliably, it performs an **ARP sweep**:

- For each IP in the subnet: “Who has this IP?”
- Devices that respond are considered present on the local network.
- After ARP identifies a device, NetPulse *also attempts* to ping it for latency (many devices block ping; that’s normal).

### 2) Identity: “MAC is the primary key”
IPs can change (DHCP), but a MAC address is a stable identity on a local network.

NetPulse uses the device’s **MAC address** as its primary identifier so:
- Same device, new IP ⇒ updates the same card
- Same IP, different MAC ⇒ treated as a different device

### 3) Monitoring: “Keep status up to date”
Once devices are known, NetPulse periodically checks them to keep status current. It updates:
- Online/offline
- Last seen time
- Latency (if pingable)
- Ping history for the graph

### 4) Vendor + hostname enrichment
- **Hostname**: reverse DNS lookup (best-effort; not all networks provide reverse DNS)
- **Vendor**: MAC OUI lookup from a local `Resources/manuf` file (Wireshark `manuf` format)

---

## Why your results may vary (networking limitations)

Local network discovery is not perfectly consistent across all routers/devices. A few important points:

### ICMP ping is often blocked
Many phones/IoT devices drop ICMP requests. That’s why NetPulse primarily uses **ARP** to detect presence, and only uses ping for latency when allowed.

### ARP is LAN-only
ARP only works within your local subnet/broadcast domain. NetPulse is designed for local LAN visualization, not internet-wide scanning.

### “Dynamic” ARP entries are normal
If you run `arp -a`, you may see entries marked `dynamic`. That simply means they were learned automatically (normal behavior).

### Private/Randomized MACs on phones
Phones often use a **randomized/private MAC** per Wi‑Fi network. The MAC you see in the phone’s “device MAC” screen may not match the MAC currently used for that SSID. NetPulse shows the MAC that is actually present on the network.

### DNS hostnames may be missing
Reverse DNS depends on your router/DNS configuration. Some networks return hostnames only for a few devices, or none at all.

---

## Configuration

### Vendor database (`Resources/manuf`)
NetPulse ships with a small starter `Resources/manuf` so vendor lookup works end-to-end.

For better coverage, replace it with the full Wireshark `manuf` file:
- Keep the filename exactly: `Resources/manuf`
- Rebuild / rerun so it copies to output

### Auto-prune
In the Settings panel:
- Enable Auto-Prune
- Choose days (default 3)
- Use **Prune now** to run immediately

Prune criteria:
- Only removes devices that are **offline** and whose `LastSeen` is older than the threshold.

---

## Project structure

- `NetPulse/Models`
  - `NetworkDevice` (persisted fields + transient runtime fields)
  - `PingPoint` (timestamp + latency; `LatencyMs = -1` is used as a “gap” marker in graph)
  - `UserSettings`
- `NetPulse/Services`
  - `ScannerService` (subnet discovery / ARP sweep / ping latency attempt)
  - `PersistenceService` (devices.json / settings.json)
  - `DnsService` (reverse DNS)
  - `VendorLookupService` (Wireshark `manuf` OUI lookup)
- `NetPulse/ViewModels`
  - `MainViewModel` (state, commands, monitoring loop, auto-prune scheduler)
- `NetPulse/Views`
  - `MainWindow.xaml` (dashboard UI)
  - `Controls/LatencyGraph` (custom real-time graph)

---

## Roadmap / Future improvements
- Vendor icons on cards (not just vendor text)
- Reduce ARP usage during monitoring (more “ping-first” strategies)
- Unit tests (mock services and test ViewModel logic)
- Packaging (MSIX), app icon, and release builds

---

## License
Choose a license before publishing (e.g., MIT). Currently: not specified.