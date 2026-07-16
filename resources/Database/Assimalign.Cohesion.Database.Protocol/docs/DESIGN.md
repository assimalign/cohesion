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

## A child root — no area dependency

This package is a child root the area root aggregates (root → Protocol, never
the reverse — the 2026-07-13 inversion; see the area DESIGN.md decision log), so
anything that only needs to speak the wire — a diagnostic tool, an alternative
client — takes this one assembly. Two consequences:

- **`ProtocolVersion` lives here**, `Current` as a plain static property. The
  struct spent a period in the area root (with `Current` grafted on from here as
  a static extension member) only because this package then referenced the root,
  making root → Protocol impossible. The inversion collapsed that split.
- **`ProtocolException` inherits `Exception`, not `DatabaseException`** (the
  repo's exception-scoping rule: keep exception inheritance local to the owning
  package). Layers that own both vocabularies translate: the server session pump
  maps it to the `ProtocolViolation` wire error in a dedicated handler; the
  client core wraps it in `DatabaseClientException`.

## Non-goals

- No transport here (TLS, sockets, pipes belong to `libraries/Connections` + the server runtime in `Database.Hosting`); `ProtocolFraming` gives stream-based reader/writer implementations any transport can wrap.
- Payload schemas land message-by-message; today: `Startup`, `Error`, `Execute` (statement + named parameters as shared tuple-codec components), `ResultHeader` (column names + shared type identity bytes), `ResultComplete` (affected count). `ResultRow` payloads are raw tuple-codec bytes — one typed component per column — so rows need no wrapper type. The MVP authenticate exchange (server build-out, #852) carries empty payloads — a trust challenge with opaque response bytes for the authenticator seam; method-specific Authenticate payload schemas and the Transaction payload arrive with later features.
- No compression negotiation in 1.0.

## AOT posture

Hand-encoded value objects, span-based codecs, no reflection.
