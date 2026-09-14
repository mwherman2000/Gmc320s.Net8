# Protocol notes

The library follows the command framing visible in the current PyGMC RFC1201 implementation: ASCII command bytes are sent as `<COMMAND>>`.

Examples:

- `<GETCPM>>` returns a 2-byte big-endian unsigned CPM value.
- `<GETVOLT>>` returns one byte representing tenths of a volt.
- `<GETTEMP>>` returns four bytes; the first two encode decimal temperature and byte 3 is a sign indicator in the PyGMC implementation.
- `<GETDATETIME>>` returns `YY MM DD HH MM SS AA`.
- `<SETDATETIME[YY][MM][DD][HH][MM][SS]>>` sets the device clock from six raw (non-ASCII) bytes and returns a single `0xAA` acknowledgement byte.
- `<GETGYRO>>` returns X/Y/Z as three big-endian signed 16-bit values followed by `AA`.
- `<GETCFG>>` returns 256 bytes. `GmcConfigParser` decodes only the leading bytes that are consistently documented across GQ RFC1201 references: byte 0 `PowerOnOff`, 1 `AlarmOnOff`, 2 `SpeakerOnOff`, 3 `GraphicModeOnOff`, 4 `BacklightTimeoutSeconds`, 5 `IdleTitleDisplayMode` - each exposed as the raw byte, without assuming a polarity for the on/off-style fields. Everything past that (notably the CPM/µSv calibration table) is firmware/model-specific and deliberately left undecoded; the full 256-byte blob is always available via `GmcConfig.Raw`. None of this is verified against physical hardware.
- `<HEARTBEAT1>>` starts a live two-byte CPS stream; the implementation uses only the low 14 bits.
- `<HEARTBEAT0>>` stops the heartbeat.
- `<GETSERIAL>>` returns 7 bytes; the device serial number is these bytes rendered as a 14-character hex string.
- `<POWEROFF>>`, `<POWERON>>` (firmware 5.71+), `<REBOOT>>`, `<FACTORYRESET>>` are fire-and-forget: the device does not send any reply.
- `<KEY0>>`/`<KEY1>>`/`<KEY2>>`/`<KEY3>>` simulate the four physical button presses; also fire-and-forget.
- `<SPIR[A2][A1][A0][L1][L0]>>` reads history data out of onboard flash. `A2A1A0` is a big-endian 24-bit start address; `L1L0` is a big-endian 16-bit length encoded as *(actual length - 1)*. The device replies with exactly the requested number of raw bytes. This length encoding is protocol-derived and not yet verified against physical hardware.
- Logged history entries: originally documented here as a guess ("one byte per sample, with `0xFF`-prefixed markers for special entries") before any real hardware data existed. That guess turned out wrong. A real GMC-320S read at history address 0 returned `55AA001A07150C272355AA0155AA001A`: a repeating `55 AA` sync prefix, not `0xFF`. Breaking it down: `55 AA 00` is followed by 6 bytes decoding as a YY/MM/DD/HH/MM/SS timestamp (`1A 07 15 0C 27 23` → 2026-07-21 12:39:35, the same layout `GETDATETIME`/`SETDATETIME` use), and `55 AA 01` appears to carry no payload before the next marker starts. Plain (non-marker) bytes are presumed to be one-byte CPM samples, consistent with community documentation of these logs generally, though none appeared in this one sample. `GmcHistoryParser.Parse` decodes marker types `0x00` and `0x01` on this basis and deliberately stops - leaving the rest as `GmcHistoryParseResult.UnparsedRemainder` - at any other marker type, since its payload length is unknown and guessing risks misreading unrelated bytes as entries. This is one observed sample, not a verified spec; other marker types almost certainly exist (notes, out-of-range CPM values, etc.).
- `FindHistoryEndAsync` is a heuristic, not a documented protocol fact: it treats a sufficiently long run of `0xFF` bytes as "erased flash past the end of logged data." The one real sample above contains no `0xFF` at all in either the markers or (by community documentation) single-byte CPM samples below 255, which supports `0xFF` as a reasonable erased-flash signal here - though a real CPM reading of exactly 255 would also produce a `0xFF` byte, so `minErasedRunLength` (default 64) exists to make a long coincidental run implausible rather than to rule it out entirely. The default 1MB (`0x100000`) upper bound is the commonly cited GMC-320 flash size, also unverified. Against real hardware this returned `0x01A3B7` (107447) well before the scan bound.

These details should be treated as protocol-derived implementation facts, while exact behavior on a particular GMC-320S firmware should be validated against physical hardware.
