# Assimalign.Cohesion.Http.Forwarded

Effective (proxy-resolved) connection identity for the Cohesion HTTP family: the
`IHttpForwardedFeature` contract and the feature-first `Effective*` read convention
(`context.EffectiveScheme` / `EffectiveHost` / `EffectiveRemoteIp` /
`EffectiveRemoteEndPoint` on `IHttpContext`, falling back to wire values when no
feature is attached).

Contract-only by design: the trust model that *produces* the feature — header
selection, `KnownProxies`/`KnownNetworks`, `ForwardLimit` — ships in
`Assimalign.Cohesion.Web.ForwardedHeaders`; the header *parsing* primitives live in
the core `Assimalign.Cohesion.Http`. Types extend the `Assimalign.Cohesion.Http`
namespace, following the `Http.Sessions`/`Http.Cookies` layering pattern.

- [docs/OVERVIEW.md](docs/OVERVIEW.md) — scope, dependencies, usage
- [docs/DESIGN.md](docs/DESIGN.md) — why the contract is its own package, and the read
  convention
