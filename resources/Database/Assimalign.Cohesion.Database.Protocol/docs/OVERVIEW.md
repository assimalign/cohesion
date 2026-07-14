# Assimalign.Cohesion.Database.Protocol — Overview

The Cohesion database wire protocol: the frame model and message contracts shared by the database server (the server runtime in `Database.Hosting`) and the client core (`Database.Client`). Pure value objects and reader/writer contracts — no sockets, no transport.

## Scope

- `ProtocolFrameHeader` / `ProtocolFrame` — 5-byte header (big-endian `u32` payload length + `u8` message type) and frame model (implemented, tested)
- `ProtocolMessageType` — startup/auth, execute, streaming results (header/row/complete), transaction control, error, liveness, terminate
- `ProtocolVersion` — negotiated at startup; owned by this package with `ProtocolVersion.Current` (1.0) as a static property — the version this assembly implements. The area root consumes it (for `IDatabaseServerSession`) through its child-root reference to this package
- `ProtocolErrorCode` — stable, append-only error codes
- `IProtocolFrameReader` / `IProtocolFrameWriter` — the transport-facing seams
- `ProtocolException` — framing violations (an independent exception root: it inherits `Exception`, not `DatabaseException`)

## Dependencies

None. This package is a **child root** of the Database area: the area root
(`Assimalign.Cohesion.Database`) references it — never the reverse — so the wire
protocol stays independently consumable.

## Consumers

The server runtime in `Database.Hosting` (frame pump into sessions) and `Database.Client` (connection core). Per-model payload encodings layer on top; the framing never changes per model.
