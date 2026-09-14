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

## Unknown: history log behavior when the flash buffer fills up

No real device tested so far has come anywhere near filling its history buffer
(~110K used of an assumed, unverified 1MB), so it's unknown whether the GMC-320S
stops logging, wraps around and overwrites the oldest entries, or does something
else once full. `FindHistoryEndAsync` assumes an erased-flash gap exists to find; a
full/wrapped buffer would look like continuous data everywhere and the probe would
just hit `maxAddress` and return `null`, with no way to distinguish that from "the
scan bound was too small." See PROTOCOL-NOTES.md for detail. At the ~1 reading/second
rate observed on real hardware, filling 1MB would take ~12 days of continuous
logging - the only known way to settle this empirically so far.
