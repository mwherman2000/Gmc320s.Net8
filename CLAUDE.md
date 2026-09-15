# Working notes for this repository

A .NET 8 client for the GQ Electronics GMC-320S Geiger counter over USB serial. Most of the
value here is not the code, which is small, but the **hardware knowledge** in
[PROTOCOL-NOTES.md](PROTOCOL-NOTES.md) - facts about a device whose maker publishes almost
nothing. Read that file before changing anything that touches the wire format.

This file covers how to work on the project. It deliberately does not repeat the protocol
findings.

## The one rule that matters: verify against hardware before concluding

Four confident conclusions were reached and then overturned during this project. Every
single one fell to a measurement, never to more careful reasoning:

| Claimed | Actually |
|---|---|
| `GETGYRO`/`GETTEMP` unsupported on this model, returning junk | Both fine; the raw bytes were well-formed all along |
| Temperature encoded as BCD (it matched ambient better) | Plain binary; a nibble of `A` in a warmed reading killed it |
| An axis "reads 1.7% low" - a gain error | Almost pure *bias*; one direction per axis cannot separate the two |
| Above 2 g the accelerometer clips | It **wraps**, so an overflowed reading comes back sign-inverted and plausible |

The pattern is identical each time: a *decoded value* looked wrong or right, and that
impression was trusted. Raw bytes never lied. So:

- Dump raw bytes with `SendRawAsync` before theorising about a decode.
- Prefer a test that can *disprove* the hypothesis. The BCD question was settled by warming
  the device until a nibble exceeded 9, not by comparing more readings to ambient.
- When a measurement overturns something, **correct the note and say what disproved it**
  rather than quietly rewriting. Several entries in PROTOCOL-NOTES.md are written this way
  on purpose; the reasoning is worth more than a tidy assertion.
- Record negative results too. Two transport optimisations were tried and measured as
  useless; they are written down so nobody repeats them.

## Measurement conventions

- **Report the minimum, not the mean**, for anything timing-related. One retried command
  costs seconds and swamps an average - an early benchmark reported 4627 ms for a command
  whose real floor was 3 ms. `--benchmark` prints min, median, mean and a slow-call count
  for exactly this reason.
- **Let the device settle.** Readings taken while it is being handled drift by as much as
  the effect being measured; a still device holds to about 0.3%, a handled one to 2.5%.
- **One variable at a time.** The device settings, the code, and the test conditions are
  all in play; changing two at once has already wasted effort here.

## Console app as the instrument

`src/Gmc320s.Console` is not a demo so much as the lab bench. Flags exist because a question
needed answering, and they are worth reusing:

```
--recent N       tail of the history log            --orientation N   accelerometer sample table
--raw CMD:BYTES  raw hex reply, repeatable          --benchmark N     per-command timing
--timeout MS     read timeout, to unmask stalls     --full-scan       walk the whole log (slow)
--key 0-3        simulate a button press
```

Only read commands are wired up. `SetDateTime`, `PowerOff/On`, `Reboot` and `FactoryReset`
are deliberately unreachable - a menu factory reset already destroyed ~109k logged readings
once, and a demo people run casually should not be able to trigger that.

## Command traps

Consult PROTOCOL-NOTES.md for detail, but be aware these exist:

- **`GETCPM`** cannot be polled fast. A too-soon request is silently ignored and the retry
  hides a full 5 s stall.
- **`GETTEMP`** blocks ~170 ms for the sensor conversion and has two known-bad reply values,
  both rejected by `GetTemperatureCelsiusAsync`.
- **`GETGYRO`** is an accelerometer, exposed as `GetOrientationAsync`. A single sample near
  1 g does **not** mean the device is still - measured ~10% false positives under motion.
  Use `GetStableOrientationAsync` when it matters.
- **History log batch length is not constant** (0 to 182 observed). Never derive entry
  positions from an assumed cadence.
- **µSv/h** comes from the device's own stored calibration, never a hardcoded sensitivity.
  If that table is unreadable the code throws rather than inventing a number - keep it that
  way.

## Testing

- `Gmc320sConnection` talks to `ISerialPort`, so `FakeSerialPort` can stand in for hardware.
  Everything except the physical device is unit-testable; there is no excuse for an
  untested parser change.
- **Pin tests to real captured bytes.** Several tests use literal byte strings taken off the
  device, including the six calibration positions and samples from a drop capture. These
  catch a firmware or endianness change loudly, which a synthetic fixture would not.
- Run `dotnet test` before committing. It is fast and there is no reason to skip it.

## Style

- Comments explain *why*, especially where a constant encodes a hardware fact - a tolerance
  is loose because one axis is 4% out, not because 0.10 looked round.
- Prefer refusing over guessing. Several methods throw rather than return a plausible wrong
  number; that is the house style, not an oversight.
- Mark derived values as `(calculated)` in console output, so device-reported and
  library-computed figures are never confused.
