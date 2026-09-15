# Backlog

## Configurable timeout/retry for port scanning and commands

`Gmc320sConnection.Command` retries a timed-out command up to 3 times (100ms between
attempts) using the connection's read timeout (default 5000ms), to smooth over
occasional device non-responses. Worst case this is ~15.2s per command, and
`Gmc320sClient.FindPortAsync` can take up to ~76s in the worst case scanning
`COM1`-`COM5` if multiple ports are open but silent.

Consider:
- Exposing `maxAttempts` / `retryDelayMs` as constructor or call parameters instead of
  the hardcoded constants in `Gmc320sConnection`.
- Using a shorter read timeout specifically during `FindPortAsync` scanning (a real
  GMC device should answer `GETVER` quickly; the long timeout is only needed for
  flaky-but-real connections during normal use).

## Resolved: GETCPM's minimum interval was measured and is now enforced

Was: "GETCPM silently ignores a request that arrives too soon; document or pace it."
Bisected with `--cpm-probe`: a 0 ms gap between calls failed roughly 80% of the time
(paying a full 5 s read timeout before the automatic retry succeeded); every gap of 1 ms
or more, up to 1100 ms tested, succeeded every time.

`GetCpmAsync` now enforces a 15 ms minimum gap internally (`Gmc320sClient.MinCpmRequestGapMs`),
sleeping first if a previous `GETCPM` reply on this client arrived more recently than that.
15 ms was chosen with margin above the validated-safe range rather than at its edge - see
the XML doc on `MinCpmRequestGapMs` for why 1-2 ms specifically would have been guessing
dressed up as a measurement (Windows timer granularity, a 5-trial sample size, and no data
at all between 2-9 ms).

**Resolved: `GETVOLT` does not need a gap.** An 8-iteration benchmark had shown 1 slow call
out of 8, ambiguous enough to warrant a real bisection rather than a guess either way.
`--cpm-probe` was generalized into `--probe COMMAND:BYTES:GAPS` (reusable for any command)
and run against `GETVOLT` at 0 ms three separate times (15 zero-gap calls total) plus 1, 5,
10, 25 and 50 ms - zero stalls anywhere, only ordinary jitter (occasional ~200 ms outliers,
nothing like GETCPM's reproducible multi-second failure). The earlier single slow call was
noise, not a systemic discard-and-retry pattern. No gap added.

`GETSERIAL`, `GETGYRO`, `GETVER` and `GETCFG` remain clean at their previously tested
intervals; `GETTEMP`'s slowness remains understood as real ADC conversion time, a different
phenomenon entirely. GETCPM is therefore the only command known to need a gap - do not add
one to anything else without measuring it the same way first.

## Unexplained: reading counts don't reconcile with elapsed time around restarts

A restart experiment (four power cycles at roughly 3, 2 and 1 minute intervals) confirmed
the qualitative behaviour - see PROTOCOL-NOTES.md - but left a quantitative anomaly.

In steady state the log rate is a clean ~1.006 readings/second: the last undisturbed
interval before the experiment was 182 entries across 179 seconds. In the restart region
the counts stop reconciling in *both* directions:

| From | To | Entries | Elapsed | Implied rate |
|---|---|---|---|---|
| 13:21:03 | 13:23:01 | 87 | 118s | 0.72/s |
| 13:24:55 | 13:25:37 | 64 | 42s | **1.48/s** |

The second row is the troubling one: more readings than seconds elapsed, which is
impossible at one reading per second. So around a restart either the logging rate changes,
the RTC is briefly wrong (plausible if it re-initialises at boot), or an anchor timestamp
means something other than "the moment this entry was written".

Two things would settle it, neither expensive:
- Restart times recorded to the **second**. The times used were rounded to the minute, so
  correlating specific anchors to specific power events carried up to 60s of slop and could
  not be used to check the arithmetic independently.
- A long undisturbed run afterwards, to confirm the ~1/second rate re-establishes itself and
  that the anomaly is confined to the restart window.

Until this is understood, do not derive a wall-clock time for an individual reading by
counting entries forward from the nearest preceding anchor. That interpolation is only
sound where the rate is known to hold, which is exactly what this anomaly puts in doubt.

## Decode the rest of the GETCFG configuration blob

Of the 256 bytes, only offsets 0-5 (named, but from GQ references rather than
observation) and 8-25 (the calibration table, verified against hardware) are
understood. Everything from 26 onward is undecoded, and offsets 6-7 are an
unconfirmed lead.

A real GMC-320S returned, for the first 32 bytes:

    00 01 01 03 01 00 | 00 64 | 06 02 00 00 20 41 ... | 00 00 00 00 3F 00
    \--- named 0-5 ---/ \-6-7-/ \---- calibration 8-25 ----/ \-- 26-31 --/

- Offsets 6-7 hold `0x0064` = 100. Sitting immediately before the calibration table,
  that looks like an alarm CPM threshold, which would also explain the `AlarmOnOff`
  flag at offset 1. Unconfirmed.
- Offset 30 holds `0x3F`, which is suggestive but meaningless in isolation.

The way to settle this is the same diffing approach that cracked the history format:
change one setting at a time from the device's own menu (alarm threshold, speaker,
backlight timeout, unit display), dump `GmcConfig.Raw` before and after, and see which
byte moved. That maps offsets to settings definitively instead of inferring them. Note
that a factory reset was already observed changing several of the named bytes, so those
offsets do carry real state - it's their exact meaning and polarity that is unverified.

The console app prints only the first 32 bytes today; a flag to dump all 256 (or write
them to a file for diffing) would make this practical.

Doing this well would also unlock the `ECFG`/`WCFG`/`CFGUPDATE` write path, which is
currently unimplemented precisely because writing bytes whose meaning is guessed would
be reckless.

## Unknown: history log behavior when the flash buffer fills up

It is still unknown whether the GMC-320S stops logging once the history buffer is
full, wraps around and overwrites the oldest entries, or does something else. No
device tested has come close: the largest log observed was ~110K of an assumed - and
still unverified - 1MB, and that log has since been erased by a device-menu factory
reset, so the current one restarted from address 0 on 2026-09-14.

Both end-of-log probes share the blind spot. `FindHistoryEndAsync` scans forward for
an erased-flash gap that a wrapped buffer would not contain. `FindHistoryEndFastAsync`
bisects, which requires the "data then erased" invariant outright; it at least checks
the tail first and returns `null` rather than bisecting when that invariant fails, but
`null` still cannot distinguish "full or wrapped" from "the scan bound was too small."

At the ~1 reading/second rate now measured exactly on real hardware, filling 1MB takes
roughly 12 days of uninterrupted logging. That remains the only known way to settle it
empirically, and it now has to start from scratch after the reset.
