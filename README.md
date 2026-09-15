# Gmc320s.Net — .NET 8 GQ GMC-320S library

A small .NET 8 library for communicating with the GQ Electronics GMC-320S over its USB serial interface using the GQ RFC1201 command protocol.

## About RFC1201

RFC1201 is GQ Electronics' own proprietary protocol, first published by them around 2012 (the spec document carries the notice "Copyright (C) GQ Electronics LLC (2012). All Rights Reserved") and revised since. It has nothing to do with the IETF's [RFC 1201](https://datatracker.ietf.org/doc/html/rfc1201) ("Transmitting IP Traffic over ARCNET Networks," 1991) - the number collision is coincidental; GQ's "RFC" is just their own internal document-naming convention, not a standards-track submission.

It's also a single-vendor protocol, not an industry standard: as far as can be determined, every device that speaks RFC1201 (or its siblings, below) is a GQ Electronics product, and every piece of software that speaks it was written specifically to talk to those devices (PyGMC, gq-gmc-control, this library, etc.). No evidence of adoption by another manufacturer or an unrelated device category was found.

GQ uses a small family of these protocols across their own measurement-device lineup:

| Protocol | Devices | Notes |
|---|---|---|
| **RFC1201** | GMC-280, GMC-300, GMC-300E, GMC-320, GMC-320+ (this library's target) | `GETCPM` replies with 2 bytes |
| RFC1801 | GMC-500, GMC-500+, GMC-600, GMC-600+, GMC-800 | Newer/higher-end counters; `GETCPM` replies with 4 bytes, otherwise similar `<COMMAND>>` framing |
| RFC1701 | EMF-360, EMF-360+, EMF-360v2, EMF-360+v2, EMF-380, EMF-380v2, EMF-390 | A different product category entirely - multi-field EMF/RF/ELF meters, not Geiger counters |

## Verification status

The implementation is based on the current PyGMC open-source implementation. PyGMC explicitly lists GMC-320S as supported and implements GMC-320-family RFC1201 communication. Its GMC-320S class uses 115200 baud. It has since been exercised against physical hardware; transient serial read timeouts are automatically retried (see [Reliability](#reliability) below).

## Supported now

- Open/close USB serial connection
- Auto-detect the device's COM port (`Gmc320sClient.FindPortAsync`)
- `GETVER`
- `GETCPM`
- `GETVOLT`
- `GETTEMP` (but see [Reading GETTEMP and GETGYRO](#reading-gettemp-and-getgyro))
- `GETDATETIME` / `SETDATETIME`
- `GETGYRO`, exposed as `GetGForceAsync` since it reports an accelerometer rather than a rate gyroscope (see [Reading GETTEMP and GETGYRO](#reading-gettemp-and-getgyro))
- `GETCFG` configuration readout, with the leading bytes parsed into `GmcConfig.Values` and the hardware-verified calibration table into `GmcConfig.Calibration` (raw 256-byte blob still available via `GmcConfig.Raw`)
- CPM-to-µSv/h conversion from the device's own stored calibration (`GetMicroSievertsPerHourAsync`, `GmcCalibration`)
- `GETSERIAL` device serial number
- `POWEROFF` / `POWERON` (firmware 5.71+) / `REBOOT` / `FACTORYRESET`
- `KEY0`-`KEY3` simulated button presses
- `SPIR` raw history readout from onboard flash (`GetHistoryAsync`), a heuristic probe for where logged data ends and erased flash begins (`FindHistoryEndAsync`, or `FindHistoryEndFastAsync` to bisect for it in ~13 reads instead of walking the log), a best-effort decoder for the observed timestamp/marker/reading log format (`GmcHistoryParser`), and direct access to the newest readings without paging the whole buffer (`GetRecentHistoryAsync`)
- RFC1201 heartbeat (`HEARTBEAT1` / `HEARTBEAT0`)
- Continuous CPS stream
- Raw command access (`SendRawAsync`)
- Async-friendly .NET 8 API with cancellation support throughout

## µSv/h conversion

Originally left unimplemented on purpose, because GQ publishes no stable source for the CPM-to-µSv/h formula and a hardcoded sensitivity would have been a guess. It is now implemented from the device's **own** stored calibration, captured out of `GETCFG` on real hardware:

| Offset | Field | Encoding | Value read |
|---|---|---|---|
| 8-9 | CPM point 1 | `uint16` big-endian | 1538 |
| 10-13 | µSv/h point 1 | `float` **little**-endian | 10.0 |
| 14-19 | CPM/µSv/h point 2 | as above | 15380 / 100.0 |
| 20-25 | CPM/µSv/h point 3 | as above | 30760 / 200.0 |

Note the mixed endianness within one table — that really is how the device stores it; big-endian floats decode to denormal garbage (~1e-41). All three points agree on **153.8 CPM per µSv/h**, i.e. 0.0065 µSv/h per CPM, the documented figure for this tube.

```csharp
var usv = await gmc.GetMicroSievertsPerHourAsync();          // reads GETCFG each call

var config = await gmc.GetConfigAsync();                     // or convert many values offline
var many = cpmValues.Select(c => GmcCalibration.ToMicroSievertsPerHour(c, config.Calibration));
```

`GmcCalibration` interpolates piecewise between the stored points, treats (0 CPM, 0 µSv/h) as an implicit origin so low counts can't yield a negative dose rate, and extrapolates along the final segment above the top point. If the table is unreadable it still throws `NotSupportedException` rather than inventing a number.

## Not yet implemented

Configuration-editing commands: `ECFG`/`WCFG`/`CFGUPDATE` (writing individual configuration bytes, e.g. alarm thresholds or calibration points). See [BACKLOG.md](BACKLOG.md) for other known follow-ups.

## Reliability

`Gmc320sConnection` retries a command up to 3 times (with a short delay and input-buffer flush between attempts) if a read times out, to smooth over occasional non-responses from the device. A persistent failure (device off, wrong port, cable unplugged) still surfaces as a `TimeoutException` after all attempts are exhausted. The live CPS heartbeat stream (`ReadCpsAsync`) gets the same per-frame retry treatment. See [BACKLOG.md](BACKLOG.md) for making the attempt count/delay configurable.

## Reading `GETTEMP` and `GETGYRO`

Both work on the GMC-320S, but their decoded values are easy to misread — this repo briefly documented them as broken before the raw bytes proved otherwise:

```
<GETTEMP>>  ->  16 00 00 AA        22, 0 tenths, positive sign, 0xAA terminator  = 22.0 °C
<GETGYRO>>  ->  0010 0030 C090 AA  X=16, Y=48, Z=-16240, 0xAA terminator
```

- **Known-bad readings are rejected for you.** The sensor occasionally answers with an impossible "negative zero" (`00 00 01 AA` — sign flag set over zero magnitude, about once in thirty reads) or with 85.0 °C, the classic power-on-reset default of digital temperature sensors. `GetTemperatureCelsiusAsync` re-reads up to three times and throws `InvalidDataException` if every attempt is implausible, so these never reach you as a plausible-looking wrong number. Retrying costs nothing when the reading is genuine, since a good value passes on the first attempt. Otherwise consecutive reads are byte-identical — a value that disagrees with one taken moments earlier is thermal lag, not a bad read, and re-reading won't fix that.
- **Readings run ~5-6 °C above room temperature.** That's self-heating inside a USB-powered enclosure, not a decode error. The encoding is plain binary and was verified as such: hand-warming produced `19 08` → `19 09` → `1A 00` (25.8 → 25.9 → 26.0 °C), and the nibble `A` in `0x1A` rules out the BCD reading that a naive ambient comparison would otherwise suggest.
- **`GETGYRO` is an accelerometer, not a rate gyroscope — scale 16384 counts per g.** The values are the static gravity vector, so a stationary device reads 1 g total, not zero. Divide by `16384.0` for g. The API calls this `GetGForceAsync`/`GmcGForce`, naming it for what it measures rather than mirroring the misleading command name.

  Six stationary samples make the scale exact. Every component is a multiple of 16, so the sensor is **12-bit left-shifted into a 16-bit field** (4096 effective steps over ±2 g) — that's what made the numbers look suspiciously round. After the shift Z lands on −1024 = −2¹⁰, i.e. −1 g. The clincher is the vector magnitude, which came to 0.991-1.020 g across all six: a stationary accelerometer *must* read 1 g since gravity never switches off, while a stationary gyroscope would read zero on every axis.

  Lying flat, X and Y within a few hundred counts of zero corresponds to under ~1° of tilt. Standing the device on edge confirmed the scale out-of-sample: Z collapsed to 0.071 g while Y rose to 0.956 g, and the implied lean of **4.4°** matched the ~5° the device was visibly tilted — a correct angle recovered from an orientation the scale factor was never fitted to.

  **Caveat for precise work: 16384 counts per g is accurate only for Z.** A full six-position calibration — each axis pointed up, then down — separates gain from offset, since `k = (r₊ − r₋)/2` and `b = (r₊ + r₋)/2`:

  | Axis | +1 g | −1 g | Gain | Offset |
  |---|---|---|---|---|
  | X | +1.006 g | −0.983 g | 0.995 | **+0.011 g** |
  | Y | +0.956 g | −0.959 g | **0.958** | −0.002 g |
  | Z | +0.996 g | −1.002 g | 0.999 | −0.003 g |

  The two bad axes fail in opposite ways: X's error is almost pure **bias** (its gain is fine), Y's is almost pure **gain** (its bias is negligible), and Z is well trimmed on both counts. One direction per axis can't distinguish the two — measuring only X-down made it look 1.7% low on gain when the gain is actually near perfect.

  For coarse "which way is up" the shared scale is fine. For anything quantitative, apply per-axis gain and offset — and let the device sit still first, since readings taken while it's being handled drifted by as much as the effect being measured.

  **Checking a reading is usable.** `GmcGForce` exposes `XG`/`YG`/`ZG`, `Magnitude`, and `IsStable`. Magnitude must be 1 g on a motionless device at any orientation, so departures measure motion — below 1 g it is accelerating downward (0 g is free fall), above 1 g it is being accelerated or arrested.

  `IsStable` is **necessary but not sufficient**, and the failure rate is not small. A moving device sweeps *through* 1 g twice per oscillation, so samples caught at those crossings look perfectly still. In a 100-sample capture of the device being shaken hard enough to saturate the sensor, **10 samples passed `IsStable`** — a one-in-ten chance that a single poll reads violent motion as a valid orientation.

  Use `GetStableGForceAsync` when it matters. It waits for several consecutive samples to agree in *direction* as well as magnitude, throws `TimeoutException` if the device never settles, and returns their average, which also cancels the ~1% per-sample noise:

  ```csharp
  var settled = await gmc.GetStableGForceAsync();   // 4 agreeing samples by default
  Console.WriteLine($"{settled.XG:F3}, {settled.YG:F3}, {settled.ZG:F3} g");
  ```

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

### Reading just the newest readings

The protocol offers no way to ask how many entries the log holds or where the last one is - there is no entry count, no write pointer, and no history-erase command; `SPIR` only does raw addressed reads. But you don't have to page through the whole buffer to reach the recent data:

```csharp
var recent = await gmc.GetRecentHistoryAsync(100);

Console.WriteLine($"Write pointer: 0x{recent.HistoryEndAddress:X6}");
foreach (var entry in recent.Entries)
    Console.WriteLine(entry);
```

This bisects for the write pointer (the log is written sequentially with erased flash after it, so "is this region erased?" is monotonic in the address), then reads backward from it and resynchronizes on a `55 AA` anchor, since a window opening mid-log may start partway through an entry.

Two things it deliberately does **not** assume:

- **How many readings sit between anchors.** A device restart writes an extra anchor and an interrupted session ends a batch early - both observed on real hardware, where batches of 180, 141 and even 0 readings all appear - so the byte count for N readings can't be computed up front. The window is grown backward until enough readings turn up.
- **That the buffer hasn't wrapped.** Bisection needs the "data then erased" invariant, so the tail is probed first and `null` is returned rather than bisecting if it isn't erased. A full or wrapped buffer therefore yields no answer instead of a confidently wrong one - wrap behavior is still untested, see [BACKLOG.md](BACKLOG.md).

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

**Confirmed exactly, after a factory reset wiped the log.** Reading the fresh log from address 0 gave a measurement the ~110K-entry log couldn't:

```
55AA00 1A090E0B321D  → 11:50:29   anchor
55AA01                            marker
55AA00 1A090E0B321D  → 11:50:29   anchor again - zero readings in between
55AA01
000000000000000000000000          12 readings
55AA00 1A090E0B3229  → 11:50:41   anchor
```

Twelve readings between timestamps exactly twelve seconds apart is **1.00 reading/second**, which turns the `Count`-not-`Cpm` naming from an inference into a measured fact. Note also the batch lengths here — **0, then 12**, against the ~180 between undisturbed anchors. That is why nothing in this library derives entry positions from an assumed batch length.

**A second finding, from scanning much further into the same device's log:** a `55 AA 00` timestamp+marker pair recurs every **exactly 182 entries** — repeated ~379 times across roughly 69,000 entries — with consecutive timestamps consistently **179 seconds apart** (occasional 178/180s jitter from whole-second RTC rounding). 182 entries = timestamp + marker + 180 plain readings, and 180 readings in ~179 seconds is ~1 reading/second. That's why `GmcHistoryReading`'s field is named `Count`, not `Cpm`: a true 60-second rolling CPM wouldn't fluctuate the way these per-entry values do, but the magnitude and volatility line up with the live `HEARTBEAT1` CPS stream from the same device. Treat this as a strong inference from timing, not a documented fact — see [PROTOCOL-NOTES.md](PROTOCOL-NOTES.md).

## Console test

```powershell
# Auto-detect the port (scans COM1-COM5)
dotnet run --project .\src\Gmc320s.Console

# Or specify a port explicitly
dotnet run --project .\src\Gmc320s.Console -- COM5

# Also simulate pressing a physical button (0-3) before starting the live CPS stream
dotnet run --project .\src\Gmc320s.Console -- COM5 --key 0

# Show more of the newest readings (default 100) - reads only the tail of the log
dotnet run --project .\src\Gmc320s.Console -- COM5 --recent 300

# Time each command against its theoretical wire time. Reports the minimum, since one
# retried call costs seconds and would swamp a mean. The overhead column shows how long
# the device itself takes to answer.
dotnet run --project .\src\Gmc320s.Console -- COM5 --benchmark 12

# Loop the orientation sensor, printing raw counts, g per axis, vector magnitude and a
# still/FALLING/ACCEL state per sample, then a settled average. Defaults to 100 samples;
# |g| should read 1.000 on a still device, so it doubles as a calibration check.
# Pass 0 to suppress the table.
dotnet run --project .\src\Gmc320s.Console -- COM5 --gforce 50

# Additionally walk the entire log and print every timestamp (one read per 4096 bytes, so slow)
dotnet run --project .\src\Gmc320s.Console -- COM5 --full-scan

# Bisect a command's minimum polling interval: COMMAND:REPLY_BYTES:comma,separated,gaps_ms.
# This is how GETCPM's "never issue two requests with no yield in between" constraint
# was found - see GetCpmAsync's XML doc and PROTOCOL-NOTES.md.
dotnet run --project .\src\Gmc320s.Console -- COM5 --probe GETVOLT:1:0,1,5,10,25,50
```

The app pauses on `Press any key to exit...` before closing, so the window stays open when launched from a debugger or by double-clicking the exe. It skips the pause automatically when input is redirected, so pipes and CI are unaffected.

## NuGet packaging

```powershell
dotnet pack .\src\Gmc320s\Gmc320s.csproj -c Release
```

This produces `Gmc320s.Net.0.1.0.nupkg`.

## License

MIT - see [LICENSE](LICENSE).

This covers this library's own code only. The RFC1201 protocol it implements is GQ Electronics LLC's own specification (copyright 2012, per the spec document itself; see [About RFC1201](#about-rfc1201)) - this project is an independent client implementation based on their published documentation and real-hardware observation, not a redistribution of GQ's spec or software.
