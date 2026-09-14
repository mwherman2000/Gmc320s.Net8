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

These details should be treated as protocol-derived implementation facts, while exact behavior on a particular GMC-320S firmware should be validated against physical hardware.
