# Assimalign.Cohesion.Web.HttpsPolicy — Design

## Design intent

One lean package pairs the two HTTPS-posture concerns a web server needs —
HTTP-to-HTTPS redirection and HTTP Strict Transport Security (issue #780). They
are a natural pair, not two micro-packages: both are edge middleware, both are
dependency-free option captures, and both turn on the same question — *is this
request secure?* HSTS and redirection are complementary halves of the same
policy (redirect the plaintext request, then tell the client never to try
plaintext again), so shipping them together keeps the composition story in one
place.

The package owns only the Web-pipeline surface: two options objects, two
`extension(IWebApplicationPipelineBuilder)` verbs, and two internal middleware.
The typed protocol values it stands on — `HttpScheme`, `HttpHost`,
`HttpHostMatcher`, `HttpHeaderKey`, the redirect status codes — all live in
`Assimalign.Cohesion.Http`.

## Connection-security detection — the transport-derived typed scheme

Both middleware must know, at request time, whether the connection is already
secure. The decision: **read `IHttpRequest.Scheme` and compare it to
`HttpScheme.Https`.** This is the typed, AOT-safe security signal #763 delivers,
and using it is the whole reason this feature was sequenced behind that work.

What #763 actually shipped, traced through the code:

- A transport listener exposes a `ConnectionSecurity` capability
  (`Capabilities.Security`); a TLS-layered listener reports
  `ConnectionSecurity.Tls`.
- The HTTP connection layer derives the request scheme from that capability once
  per listener and surfaces it as the `HttpScheme` enum on `IHttpRequest.Scheme`
  (`HttpConnectionIsSecurePropagationTests` in `Http.Connections` pins this:
  a TLS-capability listener yields `HttpScheme.Https` on every request, keep-alive
  included).

So the "typed TLS feature" the issue calls for is realized, in the shipped code,
as the transport-derived `HttpScheme` enum on the request — **not** a separate
`ITlsConnectionFeature` in `IHttpFeatureCollection` (there is none; `IHttpConnectionInfo`
carries no security field either). Reading the enum is not scheme-string
sniffing: nothing parses the literal text `"https"`, inspects a header, or
reflects. It is the same signal `Web.Compression` already gates its BREACH
protection on (`context.Request.Scheme == HttpScheme.Https`), so this package
follows an established precedent rather than inventing a detection path.

## Redirection

### Location composition

The `Location` is rebuilt from the request itself, never echoed from an
untrusted header:

- **Scheme** — always `https`.
- **Host** — the request's own host, its inbound (plaintext) port stripped via
  `HttpHost.TryGetComponents` and replaced by the configured HTTPS port. An IPv6
  literal is re-bracketed for URL use, since the component split strips brackets
  (`[::1]:80` and the unbracketed `::1` both compose to `https://[::1]...`). A
  host that is not well-formed falls back to its raw value rather than
  manufacturing an authority.
- **Path** — preserved verbatim from `HttpPath.Value`.
- **Query** — reconstructed from the parsed `IHttpQueryCollection`, re-encoding
  each key and value with `Uri.EscapeDataString`. **This is not byte-verbatim**,
  and deliberately so: `IHttpRequest` exposes only the *parsed* query (the raw
  query string is discarded at the transport boundary — see
  `TransportHttpRequest`), so the reconstruction (a) follows the parsed
  collection's enumeration order rather than guaranteed wire order, and (b)
  normalizes percent-encoding. For a redirect this is safe — the client re-issues
  against the rebuilt target — and it is the best fidelity the current
  `IHttpRequest` surface allows. Exposing a raw request-target/raw-query accessor
  on `IHttpRequest` (a core `Http` change) would let this be truly verbatim; it
  is recorded as a scope-creep candidate, not taken here.

The response is **status + `Location` only** — bodyless. Being a `3xx`, it sits
below the `4xx`/`5xx` range the status-code-pages middleware acts on, so that
middleware never adds a body to it; no coordination is needed beyond choosing a
redirect status.

### Method-preserving status only

`307` (default) and `308` are the only permitted statuses, validated at builder
time. The historical `301`/`302` are rewritten to `GET` by user agents, which
would silently drop the method and body of a non-`GET` request being upgraded to
HTTPS (RFC 9110 §15.4.8/§15.4.9). Restricting the option to the method-preserving
pair makes that foot-gun unreachable.

### HTTPS port resolution — explicit only

`HttpsRedirectionOptions.HttpsPort` is an explicit setting (default `443`, which
is omitted from the `Location`). It is **not** derived from the server's endpoint
bindings, for a hard architectural reason: a feature-package middleware cannot
see those bindings without referencing `Web.Hosting`, and the resource
hosting-isolation rule (`COHRES001`) forbids that reference — the build fails on
it. Nor is `IHttpConnectionInfo.LocalPort` a usable fallback: on an insecure
request that is the *plaintext* port (e.g. 80), not the HTTPS port to redirect
to. No feature on the request surfaces the HTTPS listener's port. So explicit
configuration is the correct and only clean expression today; builder-time
derivation from endpoint bindings is recorded as a scope-creep candidate for if
and when the Web layer surfaces bindings to middleware through a feature.

## HSTS

### Emission point — after `next`, before the exception boundary

The `Strict-Transport-Security` field is applied **after** `next` returns
(post-`next`), and the verb documents that `UseHsts` should sit **before the
exception boundary** (`#881`) in the pipeline. This is the reasoned call the
issue asked for; the alternative (set the header before `next`) was rejected.

The forcing function is the `#881` exception boundary. On a faulted, unstarted
response it calls `response.Headers.Clear()`, renders a fresh problem+json `500`,
and **returns normally** (it swallows the fault rather than rethrowing). Trace
the two emission points against that:

- **Pre-`next`** — the header is set, then a downstream fault reaches the
  boundary, which clears all headers (wiping HSTS) and writes the `500`. The
  policy is **lost** on every error response. Rejected.
- **Post-`next`, with `UseHsts` outside the boundary** — the boundary handles the
  fault, resets, writes the `500`, and returns; control unwinds back to this
  middleware, which *then* applies HSTS onto the already-reset response. A reset
  error response served over TLS **still carries the policy** — the behavior the
  issue argues for ("a reset error response over TLS arguably still deserves
  HSTS"). Chosen.

The response model is buffered-until-the-pipeline-unwinds (the boundary and the
status-code-pages middleware both operate on the buffered response post-`next`),
so for the common buffered response the post-`next` write still lands on the head
before the transport sends it. The one response that cannot receive the field is
one whose head has **already committed** to the wire — a genuinely streamed
response. That is inherent to HTTP (no field can be added after the head is sent)
and independent of emission point; the middleware detects it through
`IHttpHeaderCollection.IsReadOnly` and **skips rather than faults**, using the
core header collection's own read-only signal so no dependency on the
response-streaming feature (`Http.Streaming`) is needed.

### Header value composed once

Because the options are captured and immutable-after-capture, the field value
`max-age=<seconds>[; includeSubDomains][; preload]` is composed **once**, in the
`UseHsts` verb, and handed to the middleware as a string. `max-age` is emitted as
whole seconds (RFC 6797 §6.1.1); the directives append in the conventional order.
`max-age=0` is valid (it tells a user agent to delete a stored policy) and
allowed; a negative `MaxAge` is rejected at builder time.

**`MaxAge` default — 365 days.** HSTS deployment guidance and the browser preload
lists treat one year as the production baseline, so that is the default. It is a
real commitment (a client that saw the header refuses plaintext for the whole
window even if HTTPS is later withdrawn), so the XML docs and this file flag the
lock-in and recommend a short window when first rolling HSTS out. `preload` is
emitted faithfully when set but its submission preconditions (≥1 year +
`includeSubDomains`) are **not** enforced, because `preload` is not itself an
RFC 6797 directive.

### Host exclusion — reuse `HttpHostMatcher`

`ExcludedHosts` reuses the core `HttpHostMatcher` (the #781 host-filtering
primitive) rather than re-implementing host matching, so exclusion shares the
same case-insensitive, port-ignoring, IPv6-bracket-insensitive semantics as the
rest of the stack — `localhost:5001` and `[::1]` match the `localhost`/`[::1]`
defaults. The default excluded set is `localhost`, `127.0.0.1`, `[::1]`: a
developer commonly serves plaintext on loopback, and a long-lived HSTS policy
pinned to `localhost` would poison every other local project on that authority.

One wrinkle drives a small design choice: `HttpHostMatcher.Create` rejects an
*empty* pattern list (the deny-all-by-mistake guard host filtering wants). HSTS
wants the opposite for an empty list — emit everywhere. So a cleared
`ExcludedHosts` compiles to a **null matcher** (no exclusions), not a call to
`Create`; a non-empty list is precompiled once at builder time, where an invalid
pattern (port-bearing, malformed, wildcard-misusing) throws.

## The RFC 6797 header-key fix (core `Http`)

`HttpHeaderKey.StrictTransportSecurity` shipped with the value
`"Strict-Transports-Security"` (an extra *s*), wrong per RFC 6797 — it would have
corrupted every HSTS emission. It is corrected to `"Strict-Transport-Security"`
in `libraries/Http/Assimalign.Cohesion.Http`, with round-trip tests through
`IHttpHeaderCollection` (mirroring the #768 `Sec-WebSocket-Protocol` fix). The
member *name* is unchanged; only the wire value moved. Blast radius was one line:
the misspelled string appeared nowhere else, and the HTTP/2 HPACK / HTTP/3 QPACK
static tables already carried the correct lowercase `strict-transport-security`.

## Ordering / composition summary

- **`UseHttpsRedirection` earliest.** An insecure request short-circuits here
  before compression, serialization, or any other response work happens on a
  response that is about to be discarded.
- **`UseHsts` before the exception boundary.** See the emission-point analysis:
  post-`next` from outside the boundary is what lets the policy survive a reset
  error response.
- The two are order-independent relative to each other. HSTS never emits on the
  redirect response anyway — that response rides an *insecure* request (a secure
  request is not redirected), and HSTS is secure-only.

## AOT posture

Options → a captured string and a precompiled matcher at registration;
request-time work is an enum comparison, an optional matcher hit, and a header
set. No reflection, no configuration binding, no service location, no runtime
code generation.

## Non-goals

- **No hosting integration.** The package must not (and cannot, per `COHRES001`)
  reference `Web.Hosting`; pipeline placement is the application's
  registration-order responsibility. HTTPS-port derivation from endpoint bindings
  is out of scope for the same reason.
- **No byte-verbatim query round-trip.** The parsed-query reconstruction is the
  ceiling of the current `IHttpRequest` surface (see "Location composition").
- **No downgrade of the redirect to `301`/`302`.** Method-preserving statuses
  only.
- **No `preload` precondition enforcement.** The directive is emitted as
  configured; submission rules are the operator's concern.
- **No redirect-host exclusions.** Redirection upgrades every insecure request;
  to avoid redirecting loopback in development, do not register the verb there
  (or serve HTTPS directly). Host *exclusion* is an HSTS concept only, because an
  HSTS policy is sticky where a redirect is not.

## Scope-creep candidates (recorded, not taken)

- Expose a raw request-target / raw query string on `IHttpRequest` so redirection
  can preserve the query byte-verbatim.
- Surface server endpoint bindings to middleware through a feature so the HTTPS
  port can be derived at builder time instead of configured explicitly.

## Testing

`tests/HttpsRedirectionTests.cs` and `tests/HstsTests.cs` drive the two
middleware directly through a unit-level `IHttpContext` double
(`tests/TestObjects/TestHttpContext.cs`) and a capturing pipeline-builder double
(`TestPipelineBuilder`), so the verbs' builder-time validation and the
middleware behavior are exercised through the public surface only — no
`Web.Hosting`/`Web.Testing` dependency. The in-memory HTTP/1.1 test factory
cannot express an `https` request (the same reason `Web.Compression` uses a
context double), which is exactly the state these tests must set. Coverage:
redirect status (`307`/`308`), `Location` composition (default/custom port,
inbound-port swap, IPv6 re-bracketing, path verbatim, query preservation and
encoding), secure pass-through, HSTS value composition for every directive
combination, the loopback exclusions and a custom wildcard exclusion, the
post-`next` emission point and its survival across a simulated `#881` reset, the
committed-head skip, and builder-time validation failures. The RFC 6797 key fix
is covered by round-trip tests in `Assimalign.Cohesion.Http`.
