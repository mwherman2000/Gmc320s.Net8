# Protocol notes

The library follows the command framing visible in the current PyGMC RFC1201 implementation: ASCII command bytes are sent as `<COMMAND>>`.

Examples:

- `<GETCPM>>` returns a 2-byte big-endian unsigned CPM value.
- `<GETVOLT>>` returns one byte representing tenths of a volt.
- `<GETTEMP>>` returns four bytes; the first two encode decimal temperature and byte 3 is a sign indicator in the PyGMC implementation.
- `<GETDATETIME>>` returns `YY MM DD HH MM SS AA`.
- `<SETDATETIME[YY][MM][DD][HH][MM][SS]>>` sets the device clock from six raw (non-ASCII) bytes and returns a single `0xAA` acknowledgement byte.
- `<GETGYRO>>` returns X/Y/Z as three big-endian signed 16-bit values followed by `AA`.
- `<GETCFG>>` returns 256 bytes.
- `<HEARTBEAT1>>` starts a live two-byte CPS stream; the implementation uses only the low 14 bits.
- `<HEARTBEAT0>>` stops the heartbeat.

These details should be treated as protocol-derived implementation facts, while exact behavior on a particular GMC-320S firmware should be validated against physical hardware.
