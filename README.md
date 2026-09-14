# Gmc320s.Net — .NET 8 GMC-320S library

A small .NET 8 library for communicating with the GQ Electronics GMC-320S over its USB serial interface using the RFC1201 command protocol.

## Verification status

The implementation is based on the current PyGMC open-source implementation. PyGMC explicitly lists GMC-320S as supported and implements GMC-320-family RFC1201 communication. Its GMC-320S class uses 115200 baud. It has since been exercised against physical hardware; transient serial read timeouts are automatically retried (see [Reliability](#reliability) below).

## Supported now

- Open/close USB serial connection
- Auto-detect the device's COM port (`Gmc320sClient.FindPortAsync`)
- `GETVER`
- `GETCPM`
- `GETVOLT`
- `GETTEMP`
- `GETDATETIME` / `SETDATETIME`
- `GETGYRO`
- `GETCFG` configuration readout, with the well-documented leading bytes parsed into `GmcConfig.Values` (raw 256-byte blob still available via `GmcConfig.Raw`)
- `GETSERIAL` device serial number
- `POWEROFF` / `POWERON` (firmware 5.71+) / `REBOOT` / `FACTORYRESET`
- `KEY0`-`KEY3` simulated button presses
- `SPIR` raw history readout from onboard flash (`GetHistoryAsync`), a heuristic probe for where logged data ends and erased flash begins (`FindHistoryEndAsync`), and a best-effort decoder for the observed timestamp/marker/reading log format (`GmcHistoryParser`)
- RFC1201 heartbeat (`HEARTBEAT1` / `HEARTBEAT0`)
- Continuous CPS stream
- Raw command access (`SendRawAsync`)
- Async-friendly .NET 8 API with cancellation support throughout

## Intentionally not guessed

µSv/h conversion is **not** silently implemented. The PyGMC project notes that GQ does not provide an official stable source for the device configuration/cpm-to-µSv/h formula and that firmware changes can affect configuration bytes. A verified calibration implementation can be added once the exact GMC-320S firmware/configuration is captured.

## Not yet implemented

Configuration-editing commands: `ECFG`/`WCFG`/`CFGUPDATE` (writing individual configuration bytes, e.g. alarm thresholds or calibration points). See [BACKLOG.md](BACKLOG.md) for other known follow-ups.

## Reliability

`Gmc320sConnection` retries a command up to 3 times (with a short delay and input-buffer flush between attempts) if a read times out, to smooth over occasional non-responses from the device. A persistent failure (device off, wrong port, cable unplugged) still surfaces as a `TimeoutException` after all attempts are exhausted. The live CPS heartbeat stream (`ReadCpsAsync`) gets the same per-frame retry treatment. See [BACKLOG.md](BACKLOG.md) for making the attempt count/delay configurable.

## Known limitation: sharing a connection across clients

`Gmc320sClient` has a public constructor that takes a `Gmc320sConnection` directly, so nothing stops you from wrapping two separate `Gmc320sClient` instances around the *same* connection. Each client has its own independent serialization gate, so the "only one operation in flight" guarantee only holds *within* a single client - it is not enforced across multiple clients sharing one connection. Only `Gmc320sConnection`'s lower-level lock (which serializes raw byte writes/reads) prevents corrupted frames in that scenario; requests from the two clients can still interleave at a higher level. Stick to one `Gmc320sClient` per `Gmc320sConnection`.

## Quick start

```csharp
using Gmc320s;

using var gmc = Gmc320sClient.Connect("COM5");

Console.WriteLine(await gmc.GetVersionAsync());
Console.WriteLine($"{await gmc.GetCpmAsync()} CPM");

await foreach (var cps in gmc.ReadCpsAsync(60))
    Console.WriteLine($"{cps} CPS");
```

### Auto-detecting the port

```csharp
using Gmc320s;

var (gmc, version) = await Gmc320sClient.FindPortAsync(onStatus: Console.WriteLine);
using (gmc)
{
    Console.WriteLine($"Found {version} on {gmc.PortName}");
}
```

### Syncing the device clock

The GMC-320S has no time zone or daylight-saving awareness — its RTC just holds whatever wall-clock time it was last set to, so it will drift an hour off at DST transitions until resynced:

```csharp
await gmc.SetDateTimeAsync(DateTime.Now);
```

### Finding where the flash history ends

```csharp
var end = await gmc.FindHistoryEndAsync();
Console.WriteLine(end is int e ? $"Logged data ends around 0x{e:X6}." : "No erased-flash gap found in the scanned range.");
```

This is a heuristic, not a protocol-documented answer: it reads flash forward in chunks looking for the first long run of `0xFF` bytes (erased flash), since GQ has never published where the real log data ends. See [PROTOCOL-NOTES.md](PROTOCOL-NOTES.md) for the caveats.

### Decoding the history log

```csharp
var history = await gmc.GetHistoryAsync(0, 4096);
var result = GmcHistoryParser.Parse(history);

foreach (var entry in result.Entries)
    Console.WriteLine(entry);
```

`GmcHistoryParser` decodes the `55 AA`-prefixed timestamp/marker scheme observed on real hardware into `GmcHistoryTimestamp`, `GmcHistoryMarker`, and `GmcHistoryReading` entries, and deliberately stops at any unrecognized marker type rather than guess a payload length - the rest of the buffer comes back as `result.UnparsedRemainder` for you to fetch more of and re-parse (prepending the remainder to the next chunk, since a marker can straddle a 4096-byte chunk boundary).

**Real hardware example.** Against an actual GMC-320S, `FindHistoryEndAsync()` returned `0x01A3B7` (107447) quickly — nowhere near the full 1MB scan bound — and the first 16 bytes read from address 0 were:

```
55AA001A07150C272355AA0155AA001A
```

No `0xFF` appears at all; instead there's a repeating `55 AA` prefix, which reads as a marker/sync sequence rather than the `0xFF`-based marker scheme this library originally guessed at (see [PROTOCOL-NOTES.md](PROTOCOL-NOTES.md)). Splitting it up:

```
55 AA 00 1A 07 15 0C 27 23   55 AA 01   55 AA 00 1A
└─ sync ┘  └── payload ───┘  └sync┘└┘   └─ sync ┘
```

`55 AA 00` followed by `1A 07 15 0C 27 23` decodes suspiciously well as a timestamp entry using the same YY/MM/DD/HH/MM/SS layout as `GETDATETIME`/`SETDATETIME`: `26 07 21 12 39 35` → 2026-07-21 12:39:35. `55 AA 01` then appears to carry no payload before the next marker starts. `GmcHistoryParser` decodes exactly this (see below); it stops rather than guess at any other marker type.

**A second finding, from scanning much further into the same device's log:** a `55 AA 00` timestamp+marker pair recurs every **exactly 182 entries** — repeated ~379 times across roughly 69,000 entries — with consecutive timestamps consistently **179 seconds apart** (occasional 178/180s jitter from whole-second RTC rounding). 182 entries = timestamp + marker + 180 plain readings, and 180 readings in ~179 seconds is ~1 reading/second. That's why `GmcHistoryReading`'s field is named `Count`, not `Cpm`: a true 60-second rolling CPM wouldn't fluctuate the way these per-entry values do, but the magnitude and volatility line up with the live `HEARTBEAT1` CPS stream from the same device. Treat this as a strong inference from timing, not a documented fact — see [PROTOCOL-NOTES.md](PROTOCOL-NOTES.md).

## Console test

```powershell
# Auto-detect the port (scans COM1-COM5)
dotnet run --project .\src\Gmc320s.Console

# Or specify a port explicitly
dotnet run --project .\src\Gmc320s.Console -- COM5

# Also simulate pressing a physical button (0-3) before starting the live CPS stream
dotnet run --project .\src\Gmc320s.Console -- COM5 --key 0
```

## NuGet packaging

```powershell
dotnet pack .\src\Gmc320s\Gmc320s.csproj -c Release
```

This produces `Gmc320s.Net.0.1.0.nupkg`.

## License

MIT - see [LICENSE](LICENSE).
