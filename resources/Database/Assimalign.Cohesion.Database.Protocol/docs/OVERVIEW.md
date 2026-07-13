# Assimalign.Cohesion.Database.Protocol — Overview

The Cohesion database wire protocol: the frame model and message contracts shared by the database server (the server runtime in `Database.Hosting`) and the client core (`Database.Client`). Pure value objects and reader/writer contracts — no sockets, no transport.

## Scope

- `ProtocolFrameHeader` / `ProtocolFrame` — 5-byte header (big-endian `u32` payload length + `u8` message type) and frame model (implemented, tested)
- `ProtocolMessageType` — startup/auth, execute, streaming results (header/row/complete), transaction control, error, liveness, terminate
- `ProtocolVersion` — negotiated at startup; `Current` is 1.0
- `ProtocolErrorCode` — stable, append-only error codes
- `IProtocolFrameReader` / `IProtocolFrameWriter` — the transport-facing seams
- `ProtocolException` — framing violations

## Dependencies

- `Assimalign.Cohesion.Database` (contract root, for the `DatabaseException` ancestry)

## Consumers

The server runtime in `Database.Hosting` (frame pump into sessions) and `Database.Client` (connection core). Per-model payload encodings layer on top; the framing never changes per model.
