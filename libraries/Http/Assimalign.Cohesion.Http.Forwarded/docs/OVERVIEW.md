# Assimalign.Cohesion.Http.Forwarded — Overview

The effective-identity contract for exchanges that crossed a proxy: what client is this
request really *from*, what scheme and host did the client really use. This package
defines the answer's shape; producing the answer is a trust decision that lives
elsewhere.

## Scope

- **`IHttpForwardedFeature`** — the per-exchange typed feature: effective
  `Scheme`/`Host`/`RemoteEndPoint` (+ `RemoteIp`/`RemotePort` conveniences), the
  original wire values, and `TrustedHopCount`.
- **`HttpContextForwardedExtensions`** — the feature-first read convention:
  `context.EffectiveScheme`, `EffectiveHost`, `EffectiveRemoteIp`, and
  `EffectiveRemoteEndPoint`, each consulting the feature and falling back to the wire
  value when no producer has attached one. These members are the repo-wide answer to
  "how do I get the client IP/scheme/host" — never re-parse forwarding headers locally.

Types extend the `Assimalign.Cohesion.Http` namespace (the `Http.Sessions` /
`Http.Cookies` pattern: separate assembly, shared namespace), so consuming code only
adds a package reference, not a new `using`.

## Dependencies

`Assimalign.Cohesion.Http` only. Optional by design — consumers that never key on
client identity behind a proxy should not reference this package.

## Producer

The forwarded-headers middleware in `Assimalign.Cohesion.Web.ForwardedHeaders`
(`UseForwardedHeaders`) evaluates the RFC 7239 / `X-Forwarded-*` chain under an
explicit trust model and attaches the feature on every exchange it processes. Without
a producer, the `Effective*` members are exactly the wire values, so they are always
safe to read.
