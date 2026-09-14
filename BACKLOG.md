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
