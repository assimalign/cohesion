# Assimalign.Cohesion.Web.ForwardedHeaders — Design

## Design intent

Cohesion's deployment story makes a proxy hop the norm: the ApplicationModel K8s
self-registry gateway and the first-party LoadBalancer/NatGateway resources put every
web service behind at least one forwarder. Without resolution, every downstream concern
— CORS origin checks, cookie `Secure`/`SameSite` decisions, session partitioning,
rate-limit keys, redirect generation, access logs — sees the proxy's IP and `http://`.
This package is the **trust half** of forwarded-header handling: it decides which
forwarded hops to believe and publishes the result. The protocol half (parsing) shipped
in `Assimalign.Cohesion.Http` (#770); the output contract lives in
`Assimalign.Cohesion.Http.Forwarded`.

## The three-package split

| Package | Owns | Why it is separate |
| --- | --- | --- |
| `Assimalign.Cohesion.Http` | The wire grammar: `HttpForwardedElementCollection` (RFC 7239), `HttpForwardedValues` (`X-Forwarded-*` lists), `HttpForwardedNode` (§6 nodes) | Protocol value objects are core primitives; parsing is a total function over hostile input with no policy in it |
| `Assimalign.Cohesion.Http.Forwarded` | The output contract: `IHttpForwardedFeature` + the `Effective*` read convention | Consumers *below* the Web area (session partitioning, rate limits, logging) reference the Http family, never an L3 resource — and per-concern feature contracts get their own `Http.<Concern>` package rather than growing the core (the `Http.Sessions`/`Http.Cookies` pattern) |
| **`Assimalign.Cohesion.Web.ForwardedHeaders`** (this package) | The policy: trust-model options, the rightmost-first walk, the middleware, `UseForwardedHeaders` | Trust is deployment topology, not wire grammar; and per the Web-area separation design the root stays contracts-only — every feature, including foundational middleware, ships as its own `Web.<Feature>` package with its verb |

Dependency direction is one-way: this package → `Web` (seams) + `Http.Forwarded`
(contract) → `Http` (primitives). The middleware never re-parses header text — it
consumes already-validated value objects, so the security-sensitive code has one job:
apply policy.

## The trust model is explicit, and header selection is part of it

`ForwardedHeadersOptions` carries four inputs, validated and snapshotted at composition
time (`UseForwardedHeaders` — no request-time configuration reads, no service
location):

- **`Headers`** (`ForwardedHeaderNames` flags) — which headers to honor. There is no
  safe default: a header the proxy does not manage arrives exactly as the attacker sent
  it, so honoring it hands over the nearest-hop position of the chain. `None` is
  rejected at composition time rather than silently no-opping (contrast ASP.NET, whose
  middleware silently does nothing until configured — a misconfiguration that surfaces
  as a subtle production identity bug instead of a thrown exception).
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

## The walk

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

## One family per exchange, RFC 7239 first

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

## Output is a feature, never mutation

`IHttpRequest.Scheme`/`Host`, the raw headers, and `IHttpContext.ConnectionInfo` are
deliberately get-only wire facts, and this middleware keeps them that way — it only
attaches an `IHttpForwardedFeature` (effective scheme/host/remote endpoint + the
original wire values + the accepted-hop count) to `IHttpContext.Features`, one instance
per exchange, on **every** exchange it sees (zero hops when nothing resolved, so
downstream reads are uniform). Downstream code uses the feature-first read convention
from `Assimalign.Cohesion.Http.Forwarded` — `context.EffectiveScheme` /
`EffectiveHost` / `EffectiveRemoteIp` / `EffectiveRemoteEndPoint`, which fall back to
wire values when the feature is absent — instead of ASP.NET-style in-place property
rewriting. The trade-off is honest: code that reads `Request.Scheme` directly does not
magically become proxy-aware; it has to opt into the effective view. In exchange, the
wire truth is never destroyed, "what did the transport actually see" stays answerable
(no `X-Original-*` header shuffling), and the resolution is observable
(`TrustedHopCount`) rather than implicit.

## Ordering contract — first position

Forwarded-headers resolution must run **before anything that consumes client
identity**: CORS, authentication, cookie policy, redirect-generating middleware, rate
limiting, access logging. Middleware execution follows registration order, so
`UseForwardedHeaders(...)` must be the first `Use` call on the pipeline. Until the
repo-wide middleware-ordering rules land (#26/#145), this contract is documentation +
XML docs on the verb; when those rules introduce enforceable ordering constraints, this
middleware is the canonical "must be first" case and should be annotated accordingly.
(Sequenced behind #26/#145 by design — do not invent a one-off enforcement mechanism
here.)

## AOT posture

Plain delegate/feature wiring over BCL `IPAddress`/`IPNetwork` and the #770 value
objects — no reflection, no runtime codegen, no dynamic serialization
(`IsAotCompatible=true`).

## Non-goals

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
