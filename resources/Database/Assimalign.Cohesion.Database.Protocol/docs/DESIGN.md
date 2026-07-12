# Assimalign.Cohesion.Database.Protocol — Design

## Intent

One wire protocol for five models. Model semantics live in message *payloads* (and the per-model clients that encode them), never in the framing — so the server, connection pooling, TLS, auth, and result streaming are built once. This is the same shape as PostgreSQL's protocol (one framing, many statement kinds), chosen over per-model protocols which would quintuple the security and compatibility surface.

## Framing

Every frame is `u32 payload-length (big-endian) + u8 message-type + payload`. Decisions:

- **Length-prefixed, not delimited** — zero scanning, predictable buffering over `Connections` pipes.
- **Big-endian** — network order, matching every mainstream database protocol; the header codec is the only place byte order appears.
- **`MaxPayloadLength` (16 MiB) enforced at parse** — an untrusted length prefix must never drive allocation. Larger logical payloads (blob streams, large rows) are chunked across frames; this mirrors the HTTP/2 lesson (#750) that unbounded peer-driven buffering is a DoS primitive.
- **Type byte after length** — the reader can reject oversized frames before dispatching on type.

## Message flow

`Startup → Authenticate → AuthenticateResponse → Ready`, then request/response: `Execute → ResultHeader → ResultRow* → ResultComplete`, with `Transaction` frames for explicit transaction control and `Error` terminating any exchange. `Ping`/`Pong` keep idle connections verifiable; `Terminate` ends a session cleanly. Extended-protocol statements (parse/bind/execute split for prepared statements) are a planned minor-version addition — the type enum leaves room.

## Versioning

`ProtocolVersion` (major.minor) travels in `Startup`. Minor versions are additive (new message types, new payload fields with defaults); major versions may change framing. Servers reject unknown majors with `UnsupportedVersion`. `ProtocolErrorCode` values are append-only and never renumbered — they are part of the contract clients program against.

## Non-goals

- No transport here (TLS, sockets, pipes belong to `libraries/Connections` + `Database.Server`); `ProtocolFraming` gives stream-based reader/writer implementations any transport can wrap.
- Payload schemas land message-by-message; today: `Startup`, `Error`, `Execute` (statement + named parameters as shared tuple-codec components), `ResultHeader` (column names + shared type identity bytes), `ResultComplete` (affected count). `ResultRow` payloads are raw tuple-codec bytes — one typed component per column — so rows need no wrapper type. Authenticate/Transaction payloads arrive with the server build-out.
- No compression negotiation in 1.0.

## AOT posture

Hand-encoded value objects, span-based codecs, no reflection.
