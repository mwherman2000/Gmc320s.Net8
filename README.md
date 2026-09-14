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
- `GETCFG` raw 256-byte configuration
- RFC1201 heartbeat (`HEARTBEAT1` / `HEARTBEAT0`)
- Continuous CPS stream
- Raw command access (`SendRawAsync`)
- Async-friendly .NET 8 API with cancellation support throughout

## Intentionally not guessed

µSv/h conversion is **not** silently implemented. The PyGMC project notes that GQ does not provide an official stable source for the device configuration/cpm-to-µSv/h formula and that firmware changes can affect configuration bytes. A verified calibration implementation can be added once the exact GMC-320S firmware/configuration is captured.

## Not yet implemented

Other commands in the RFC1201 family that this library doesn't expose yet: `GETSERIAL`, `POWEROFF`/`POWERON`, `REBOOT`, `FACTORYRESET`, `KEY0`-`KEY3` (simulated button presses), `SPIR` (reading logged history out of onboard flash), and `ECFG`/`WCFG`/`CFGUPDATE` (writing individual configuration bytes, e.g. alarm thresholds or calibration points). See [BACKLOG.md](BACKLOG.md) for other known follow-ups.

## Reliability

`Gmc320sConnection` retries a command up to 3 times (with a short delay and input-buffer flush between attempts) if a read times out, to smooth over occasional non-responses from the device. A persistent failure (device off, wrong port, cable unplugged) still surfaces as a `TimeoutException` after all attempts are exhausted. See [BACKLOG.md](BACKLOG.md) for making the attempt count/delay configurable.

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

## Console test

```powershell
# Auto-detect the port (scans COM1-COM5)
dotnet run --project .\src\Gmc320s.Console

# Or specify a port explicitly
dotnet run --project .\src\Gmc320s.Console -- COM5
```

## NuGet packaging

```powershell
dotnet pack .\src\Gmc320s\Gmc320s.csproj -c Release
```

This produces `Gmc320s.Net.0.1.0.nupkg`.
