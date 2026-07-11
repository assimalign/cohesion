# Assimalign.Cohesion.Web — Design

## Design intent

`Assimalign.Cohesion.Web` is the Web area root. It owns two things and resists owning
more:

1. **The composition seams** every Web library builds against: the application/builder
   contracts (`IWebApplication`, `IWebApplicationBuilder`), the middleware-first pipeline
   (`IWebApplicationPipeline`, `IWebApplicationPipelineBuilder`,
   `IWebApplicationMiddleware`, the `WebApplicationMiddleware` delegate), and the server
   seam (`IWebApplicationServer`). Feature packages compose against these; the runtime
   module (`Web.Hosting`) implements them; the build-enforced hosting-isolation rule
   (`resources/Web/README.md`, `.claude/rules/resource-areas.md`) keeps the two
   directions from ever meeting in a library's dependency graph.
2. **Foundational middleware** — pipeline stages that are input to *most other
   middleware* rather than features of their own. Today that is exactly one thing:
   forwarded-headers resolution. The bar for adding another is that same test — "do
   CORS, authentication, cookie policy, and logging all consume its output?" — not
   convenience.

The root references `Assimalign.Cohesion.Http` and nothing else. No DI, no
configuration, no logging: composition integration is `Web.Hosting`'s one job
(`hosting-di-philosophy`), and the root must stay importable by every feature library
without dragging a composition surface along.

## The pipeline model (middleware-first)

The Web area composes request handling as an onion of middleware over `IHttpContext` —
fluent `.Use(...)` registration, `Task InvokeAsync(IHttpContext, WebApplicationMiddleware next)`
execution, registration order = execution order. There is deliberately no return-value
result model (the `IResult` abstraction was withdrawn pre-merge, 2026-07-10 direction):
middleware either writes the response and stops calling `next`, or cooperates by
attaching typed features to `IHttpContext.Features` for downstream stages. That
feature-collection seam — not request-time service location — is the area's
extensibility mechanism, which is why the pipeline contracts here stay this small.

## Forwarded-headers resolution (#778)

### Why it lives in the root

Cohesion's deployment story makes a proxy hop the norm: the ApplicationModel K8s
self-registry gateway and the first-party LoadBalancer/NatGateway resources all put
every web service behind at least one forwarder. Without resolution, every downstream
concern — CORS origin checks, cookie `Secure`/`SameSite` decisions, session
partitioning, rate-limit keys, redirect generation, access logs — sees the proxy's IP
and `http://`. That "consumed by nearly everything" position is precisely the root's
foundational-middleware bar, and the lean-tree rule cuts against a micro-package for
one middleware + one options type. The protocol half (parsing) stayed in
`Assimalign.Cohesion.Http` (#770); this is the trust half.

### The split: parsing there, policy here, contract between

- **`Assimalign.Cohesion.Http`** owns the wire grammar: `HttpForwardedElementCollection`
  (RFC 7239), `HttpForwardedValues` (`X-Forwarded-*` lists), `HttpForwardedNode`
  (§6 node identifiers). The middleware never re-parses header text — it consumes these
  primitives, so the security decision (which hops to believe) is applied to
  already-validated data.
- **`Assimalign.Cohesion.Http` also owns the output contract**: `IHttpForwardedFeature`
  and the `context.Effective*` read convention. The contract sits in the Http core —
  not here — because consumers below the Web area (`Http.Sessions` partitioning,
  future rate limiting, any L1 library that keys on client identity) can reference the
  Http core but must never reference an L3 resource. The Web root is the contract's
  *producer*; the contract's *home* is where every consumer can see it.
- **This library** owns the policy: the trust model, the walk, and the middleware.

### The trust model is explicit, and header selection is part of it

`ForwardedHeadersOptions` carries four inputs, validated and snapshotted at composition
time (`UseForwardedHeaders` — no request-time configuration reads, no service location):

- **`Headers`** (`ForwardedHeaders` flags) — which headers to honor. There is no safe
  default: a header the proxy does not manage arrives exactly as the attacker sent it,
  so honoring it hands over the nearest-hop position of the chain. `None` is rejected at
  composition time rather than silently no-opping (contrast ASP.NET, whose middleware
  silently does nothing until configured — a misconfiguration that surfaces as a subtle
  production identity bug instead of a thrown exception).
- **`KnownProxies` / `KnownNetworks`** — the trust boundary, as exact addresses and CIDR
  ranges (`System.Net.IPNetwork`). Defaults: the IPv4/IPv6 loopback addresses, nothing
  else. IPv4-mapped IPv6 addresses are normalized to IPv4 on both sides of every
  comparison.
- **`ForwardLimit`** (default `1`) — how many hops may be accepted. The default trusts
  exactly the entry appended by the directly connected proxy; raising it is an explicit
  statement about how many trusted forwarders are actually chained.
- **`TrustLocalTransports`** (default `true`) — whether a *non-IP* transport peer (Unix
  domain socket, named pipe, in-memory) is trusted as the first hop. Cohesion ships
  those drivers (#773) and a local reverse proxy forwarding over IPC is the canonical
  topology they exist for; a non-IP endpoint is machine-local by construction, so the
  default mirrors the loopback default. `DnsEndPoint` is excluded from the category and
  is never trusted. This knob is also why the in-memory `Web.Testing` factory can drive
  the middleware end to end.

### The walk

Entries are evaluated **rightmost-first** — nearest hop first, per the traversal
affordances the #770 primitives expose (`Reverse`, `Nearest`, wire-order indexing):

1. Entry *r* applies only if the peer that handed it over is trusted: the transport
   peer for *r* = 0, the address adopted from entry *r*−1 after that. The first
   untrusted peer stops the walk; everything to its left is attacker-writable noise.
2. A hop asserting anything malformed — an unparseable `X-Forwarded-For` node, a
   `proto` other than `http`/`https`, a `host` that fails a shape check (separators,
   whitespace, quotes and friends are rejected; *which* hosts are acceptable is a
   consumer allowlist concern, not shape validation) — stops the walk **before any of
   that hop's values apply**. Already-accepted hops stay applied; they were vouched for
   independently.
3. A hop whose `for` discloses no address (`unknown`, an obfuscated §6.3 identifier, or
   no `for` at all) applies its scheme/host values — the trusted peer vouched for the
   whole entry — but ends the walk: with no address, nothing can vouch for deeper
   entries. Corollary: without an `X-Forwarded-For` chain in play, `X-Forwarded-Proto`
   and `X-Forwarded-Host` are believed at most **one** hop deep regardless of
   `ForwardLimit` (stricter than ASP.NET, which re-checks the unchanged connection
   address and can walk deeper on proto alone).
4. Within a hop, later (nearer-client) applications overwrite earlier ones, so the
   deepest *accepted* entry wins — the closest the walk verifiably gets to the client.

### One family per exchange, RFC 7239 first

The two header families cannot be correlated hop-for-hop (a proxy writes one of them,
and their entry counts need not agree), so mixing them within one exchange would let
trust established by one family launder values from the other. Per exchange the
resolver picks **one source**: if `Forwarded` is honored and present, it is exclusive;
otherwise the honored `X-Forwarded-*` headers are used (correlated from the right,
tolerating asymmetric lengths). A present-but-unusable `Forwarded` header — the #770
parser is deliberately strict and all-or-nothing — **poisons resolution entirely**
rather than falling back to the legacy family: a malformed header must never buy the
sender a different evaluation path. The precedence is a fixed, documented policy rather
than an option; a knob can be added compatibly if a real topology ever needs
legacy-first.

### Output is a feature, never mutation

`IHttpRequest.Scheme`/`Host`, the raw headers, and `IHttpContext.ConnectionInfo` are
deliberately get-only wire facts, and this middleware keeps them that way — it only
attaches an `IHttpForwardedFeature` (effective scheme/host/remote endpoint + the
original wire values + the accepted-hop count) to `IHttpContext.Features`, one instance
per exchange, on **every** exchange it sees (zero hops when nothing resolved, so
downstream reads are uniform). Downstream code uses the feature-first read convention —
`context.EffectiveScheme` / `EffectiveHost` / `EffectiveRemoteIp` /
`EffectiveRemoteEndPoint`, which fall back to wire values when the feature is absent —
instead of ASP.NET-style in-place property rewriting. The trade-off is honest: code
that reads `Request.Scheme` directly does not magically become proxy-aware; it has to
opt into the effective view. In exchange, the wire truth is never destroyed, "what did
the transport actually see" stays answerable (no `X-Original-*` header shuffling), and
the resolution is observable (`TrustedHopCount`) rather than implicit.

### Ordering contract — first position

Forwarded-headers resolution must run **before anything that consumes client
identity**: CORS, authentication, cookie policy, redirect-generating middleware, rate
limiting, access logging. Middleware execution follows registration order, so
`UseForwardedHeaders(...)` must be the first `Use` call on the pipeline. Until the
repo-wide middleware-ordering rules land (#26/#145), this contract is documentation +
XML docs on the verb; when those rules introduce enforceable ordering constraints, this
middleware is the canonical "must be first" case and should be annotated accordingly.
(Sequenced behind #26/#145 by design — do not invent a one-off enforcement mechanism
here.)

### AOT posture

Plain delegate/feature wiring over BCL `IPAddress`/`IPNetwork` and the #770 value
objects — no reflection, no runtime codegen, no dynamic serialization
(`IsAotCompatible=true`).

### Non-goals

- **Producing forwarding headers.** Header *injection* belongs to the LoadBalancer
  resource's data plane when it is implemented, not to the consumer middleware.
- **Host allowlisting.** The shape check on forwarded hosts prevents smuggling; deciding
  which hosts are acceptable for redirects/links is a consumer policy (a future
  host-filtering concern), not identity resolution.
- **Vendor headers** (`X-Real-IP`, `X-Forwarded-Port`, …) and custom header names. The
  scope is RFC 7239 plus the ubiquitous trio, mirroring the #770 primitives' scope.
- **De-obfuscating RFC 7239 identifiers.** An obfuscated node ends the walk; reversing
  it is private to the proxy that issued it.
- **Mutating wire state.** No seam exists for it on purpose; see above.
