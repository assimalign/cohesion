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

## Scope: in-process only

This pass completes the **in-process** session surface. The package does not
ship a backing-store abstraction, a session-id cookie, or middleware that
resolves a session per request. `HttpSessionOptions` exists as the documented
home for session configuration (cookie name, idle timeout) so those knobs
have a stable place to live when store/cookie wiring lands, but the options
do not bind the package to any store today.

`LoadAsync` / `CommitAsync` are part of the contract precisely so a future
distributed store can round-trip through a backend without changing callers;
the in-memory implementation treats them as no-ops (aside from honoring
cancellation and flipping `IsAvailable`).

## AOT posture

No reflection, no runtime code generation, no dynamic serialization. The
store is a plain dictionary; typed accessors use `Encoding.UTF8` and
`BinaryPrimitives`. Fully AOT/trim safe.

## Non-goals

- **Distributed / backing store.** No `IHttpSessionStore` abstraction in this
  pass; the in-memory store is the only implementation.
- **Session-id cookie establishment.** No cookie is read or written; the
  Cookies dependency is intentionally not taken here.
- **Per-request session middleware.** Resolving and committing a session
  around each request is a higher-layer concern.
- **Rich typed accessors beyond int/string.** Other scalar types can layer on
  as additional extensions following the same encoding discipline.
