# Assimalign.Cohesion.Http.Sessions — Design

Per-exchange session state for the Cohesion HTTP family. This document
captures the design intent so future readers do not have to re-derive it from
the code.

## Design intent

Restore the familiar `ISession`-style ergonomics (`context.Session`, typed
get/set, key enumeration) on top of the Cohesion HTTP protocol core, which
deliberately omits sessions — they are an application-layer concept, not part
of the wire protocol. The package attaches an `IHttpSessionFeature` to
`IHttpContext.Features` and surfaces it through `context.Session` /
`context.RequireSession`.

## Surface shape: binary store + typed skin

`IHttpSession` is a binary key/value store (`byte[]` values) plus lifecycle
(`LoadAsync` / `CommitAsync`), availability (`IsAvailable`), and key
enumeration (`Keys`). The binary core is the storage primitive; typed
ergonomics (`GetString`/`SetString`, `GetInt32`/`SetInt32`, and their
`TryGet` forms) are **extension members on `IHttpSession`**, not instance
methods.

Putting the typed accessors on the interface as extensions (rather than on
the concrete `HttpSession`) means they apply to *every* session
implementation — a future distributed or cookie-backed store inherits the
exact same typed API for free, and the encoding rules live in one place.

### Encoding rules

- **Strings**: UTF-8.
- **Int32**: fixed big-endian (network order) via
  `BinaryPrimitives.WriteInt32BigEndian`. A stable, endian-independent byte
  layout means a value written on one platform reads back identically on
  another, and lets a different store interoperate by following the same
  convention. `GetInt32` returns `null` when the stored value is not exactly
  four bytes, so a string value is never silently reinterpreted as an int.

## IsAvailable semantics

`IsAvailable` follows the conventional `ISession` contract: it is `false`
until `LoadAsync` completes, then `true`. The in-memory `HttpSession` imposes
no real load step, so reads and writes succeed before `LoadAsync` is called;
`IsAvailable` simply reports whether a load has occurred. A distributed store
that genuinely needs an async fetch will gate readiness on the same flag
without changing the contract.

## Feature + context ergonomics

`IHttpSessionFeature` carries the session on the context. The
`context.Session` extension property reads through the feature (returns
`null` when none is installed) and, on assignment, installs the internal
`HttpSessionFeature`. `context.RequireSession` throws when no session has
been attached, for call sites that treat the session as a hard dependency.
Storage is the strongly-typed `IHttpContext.Features` collection (not the
loose `Items` bag) so the feature can be observed or replaced by middleware.

## Backing-store seam: `IHttpSessionStore`

`LoadAsync` / `CommitAsync` were always part of the contract so a future
distributed store could round-trip through a backend without changing callers.
That store seam now exists: **`IHttpSessionStore`** is the asynchronous
backing-store contract — `GetAsync` / `SetAsync` / `RefreshAsync` /
`RemoveAsync`, keyed by session id, carrying an opaque `byte[]` payload plus
idle-timeout metadata. The store never interprets the payload; framing is the
session's concern (see below). `InMemoryHttpSessionStore` is the default
implementation — the in-memory dictionary moved *behind* the seam — so the same
store contract that a distributed backend (distributed cache, key/value
resource, database) will implement already runs in-process. The standalone
`HttpSession` remains the simple, no-store, in-process session for direct/manual
use; the store-backed session (`HttpSessionStoreSession`, internal) is what the
Web session middleware installs over the configured store.

### Payload framing — `HttpSessionSerializer`

Any store must round-trip **identical bytes**, so the dictionary→bytes framing
lives with the session, not the store. `HttpSessionSerializer` is a
version-prefixed, length-prefixed binary layout (`[version][count]` then
`[keyLen][key][valLen][value]` per entry, big-endian, UTF-8 keys). It uses no
reflection and no dynamic serialization — fully AOT/trim-safe — and the leading
version byte lets the frame evolve: an unrecognized version is rejected
(`TryDeserialize` returns `false`) rather than misread. A store sees only opaque
bytes, so switching backends never changes the frame.

### Concurrency contract — last-commit-wins

The store performs **no** read-modify-write locking across a request. Two
concurrent requests for the same session id each read a snapshot, mutate
locally, and write the whole payload back; the write that completes last
replaces the payload **wholesale** — there is no per-key merge. This is the
contract every `IHttpSessionStore` implementation must honor (`SetAsync` is an
unconditional overwrite), chosen because it is what distributed stores can
implement cheaply and uniformly. Compare-and-swap / ETag concurrency is
deliberately *not* part of this contract; a backend that offers it may expose
stronger guarantees through a separate adapter, but the session pipeline relies
only on last-commit-wins. `HttpSessionStoreSession.CommitAsync` writes only when
the session was modified; an accessed-but-unmodified session instead slides the
store's idle window.

### Sliding expiration

Idle expiration is **renew-on-access** at the store level, driven by
`HttpSessionOptions.IdleTimeout`: `GetAsync` and `RefreshAsync` both push a live
entry's expiry to `now + idleTimeout`, and an elapsed entry reads back as absent.
The in-memory default expires lazily (on access) with no background reaper.

### What still lives elsewhere

`HttpSessionOptions` remains the store-agnostic home for cookie/timeout
configuration (cookie name, path, `HttpOnly`, idle timeout) and does not bind
this package to any store. The session-id **cookie establishment**, the
**per-request middleware** (`UseSessions`), and the **id-regeneration** API live
one layer up in `Assimalign.Cohesion.Web.Sessions`, which composes this store
seam with the hardened `Http.Cookies` model — this package still takes no cookie
dependency.

## AOT posture

No reflection, no runtime code generation, no dynamic serialization. The
in-memory store is a plain `ConcurrentDictionary`; typed accessors use
`Encoding.UTF8` and `BinaryPrimitives`; payload framing is hand-rolled
length-prefixed binary (`HttpSessionSerializer`). The clock is a
`TimeProvider` (BCL). Fully AOT/trim safe.

## Non-goals

- **Concrete distributed backends.** The store *seam* ships here with an
  in-memory default; a distributed cache / key-value / database store is a
  follow-up adapter that implements `IHttpSessionStore` (honoring the
  last-commit-wins contract), not part of this package.
- **Session-id cookie establishment.** No cookie is read or written *in this
  package*; the Cookies dependency is intentionally not taken here. Cookie
  establishment lives in `Assimalign.Cohesion.Web.Sessions`.
- **Per-request session middleware.** Resolving and committing a session
  around each request is a higher-layer concern (`UseSessions` in
  `Web.Sessions`).
- **Compare-and-swap / merge concurrency.** The store contract is
  last-commit-wins by design; per-key merge or optimistic concurrency is not
  offered at this seam.
- **Rich typed accessors beyond int/string.** Other scalar types can layer on
  as additional extensions following the same encoding discipline.
