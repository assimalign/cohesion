# Assimalign.Cohesion.Http.Forwarded — Design

## Design intent

Behind a reverse proxy, the transport-level surfaces (`IHttpContext.ConnectionInfo`,
`IHttpRequest.Scheme`/`Host`) describe the proxy-to-server leg; the client's identity
travels in attacker-writable forwarding headers that must pass a trust model before
they are believed. This package owns the **output contract** of that evaluation —
`IHttpForwardedFeature` — and the **read convention** downstream code uses to consume
it. It contains no parsing (core `Assimalign.Cohesion.Http`) and no trust policy
(`Assimalign.Cohesion.Web.ForwardedHeaders`): it is the seam between the two.

## Why a dedicated package (not the core, not the Web area)

- **Not the core `Assimalign.Cohesion.Http`:** the core carries the protocol — value
  objects with exactly one correct parse. Per-concern feature contracts follow the
  established layering pattern instead: their own `Http.<Concern>` assembly
  (`Http.Sessions`, `Http.Cookies`, `Http.Forms`, …) so the core's surface stays
  protocol-only and a concern's contract is an opt-in reference, not a tax on every
  Http consumer. This package is that pattern applied to effective identity.
- **Not the Web area:** consumers of effective identity sit *below* L3 — session
  partitioning in `Http.Sessions`, rate-limit keys, access logging — and an L1 library
  must never reference a resource area. The contract's home is where every consumer can
  see it; the Web middleware is merely its producer.

The result is a one-way chain with no cycles:
`Web.ForwardedHeaders` (policy/producer) → `Http.Forwarded` (contract) →
`Http` (primitives).

## Namespace: extends `Assimalign.Cohesion.Http`

Like every `Http.<Concern>` package, types declare `namespace Assimalign.Cohesion.Http`
(the csproj marks it with `RootNamespace`). The feature is discovered from
`IHttpContext.Features` next to the other feature contracts, and the `Effective*`
members appear on `IHttpContext` with no extra `using` — assembly granularity without
namespace fragmentation, mirroring how the BCL splits `System.*`.

## The read convention (the get-only-surfaces decision)

`IHttpRequest.Scheme`/`Host` and `IHttpContext.ConnectionInfo` are deliberately
get-only, and forwarded resolution never mutates them — there is no ASP.NET-style
in-place rewrite seam in Cohesion on purpose. The decided convention is
**feature-first with wire fallback**:

- `context.EffectiveScheme` / `EffectiveHost` / `EffectiveRemoteIp` /
  `EffectiveRemoteEndPoint` consult `IHttpForwardedFeature` and fall back to the wire
  values when absent, so they are always safe to read — in a proxy-less deployment
  "effective" and "wire" are the same thing.
- A producer attaches the feature on **every** exchange it processes, with
  `TrustedHopCount == 0` when nothing resolved, so reads behind the middleware are
  uniform and the resolution outcome is observable rather than implicit.
- The trade-off is honest: code reading `Request.Scheme` directly does not magically
  become proxy-aware — it opts into the effective view. In exchange the wire truth is
  never destroyed and "what did the transport actually see" stays answerable
  (`Original*` members carry it too).

## Contract shape

Interface-first: `IHttpForwardedFeature : IHttpFeature` is pure data (effective +
original values + hop count) with no behavior to inject; the producing middleware keeps
its implementation `internal`. Another producer (a different server host, a test
harness) implements the interface directly — it is deliberately trivial to fulfill.

## AOT posture

Contract + extension properties over core value types — no reflection, no runtime
codegen (`IsAotCompatible=true`).

## Non-goals

- **Parsing.** `HttpForwardedElementCollection`/`HttpForwardedValues`/
  `HttpForwardedNode` live in the core; this package never touches header text.
- **Trust policy.** `KnownProxies`/`KnownNetworks`/`ForwardLimit`/header selection and
  the rightmost-first walk belong to `Web.ForwardedHeaders`.
- **Mutation of wire state.** The read convention exists precisely so nothing ever
  rewrites `Scheme`/`Host`/`ConnectionInfo`.
