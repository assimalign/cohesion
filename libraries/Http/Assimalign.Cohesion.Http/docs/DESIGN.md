# Assimalign.Cohesion.Http — Design

The HTTP protocol core: wire-level request/response/header/field models shared
by every transport, client, and server in the family. This document captures
the design decisions behind the parts of the core that are easy to get wrong;
it grows as areas are touched rather than attempting to re-document the whole
surface at once.

## Field sections, repeated fields, and trailers

### The model

HTTP messages carry two ordered **field sections**: the header section before
the body and an optional **trailer section** after it (RFC 9110 §6.3, §6.5).
The core models them with the same primitive — `IHttpHeaderCollection` /
`HttpHeaderValue` — because a trailer field is structurally a header field; what
differs is the *lifecycle* and the *eligibility rules*, not the representation.

- **Headers** are available as soon as the message head is parsed and live on
  `IHttpRequest.Headers` / `IHttpResponse.Headers`.
- **Trailers** are a separate section modeled with their own collection type,
  `IHttpTrailerCollection : IHttpHeaderCollection`, surfaced directly on
  `IHttpRequest.Trailers` / `IHttpResponse.Trailers`.

### Why trailers belong on the core message interfaces (not behind a feature)

Cookies, sessions, forms, and antiforgery are *layered application* semantics,
so they live behind features and extension members and the core message
interfaces stay free of them. Trailers are different in kind: they are
**wire-level message structure** defined by RFC 9110 §6.5 and carried by every
HTTP version — chunked transfer in HTTP/1.1 (RFC 9112 §7.1.2) and the trailing
HEADERS field section in HTTP/2 (RFC 9113 §8.1) and HTTP/3 (RFC 9114 §4.1).
The principled line the core draws is therefore: *what every HTTP message
structurally has* (`Headers`, `Body`, `Trailers`) belongs on the core
request/response interfaces; *layered semantics* belong in features. Trailers
sit on the former side, beside `Headers`.

`IHttpTrailerCollection : IHttpHeaderCollection` is the natural shape — a
trailer section *is* a field section, so it reuses the entire header surface
(get/set/add/remove/enumerate) and adds only `IsSupported`. A trailer
collection can be passed anywhere a header collection is expected (shared
rendering logic, the `HttpFieldRules` classifier, etc.).

### `IsSupported` and failing loudly

`IsSupported` is a **capability** signal: whether this exchange surfaces a
trailer section (HTTP/1.1 chunked, or HTTP/2 / HTTP/3 → yes; a non-chunked
HTTP/1.1 message → no). When `false`, `HttpTrailerCollection` is empty and
**mutation throws** `InvalidOperationException`, so a server that adds trailers
to an exchange that physically cannot transmit them fails at the point of
addition rather than silently dropping them on the wire. The shared
`HttpTrailerCollection.Unsupported` singleton is the default.

### Interface evolution via a default member

`IHttpRequest.Trailers` / `IHttpResponse.Trailers` are **default interface
members** that return `HttpTrailerCollection.Unsupported`. This let the trailer
section be added to the core message model without breaking the many existing
`IHttpRequest` / `IHttpResponse` implementations (test doubles, adapters): they
inherit the safe unsupported default. The abstract `HttpRequest` / `HttpResponse`
bases override it with a concrete `HttpTrailerCollection` property (and explicit
interface mapping), and the transports override *that* where they actually
surface trailers — HTTP/1.1 attaches a supported, populated collection for a
chunked request's parsed trailer section.

### Repeated fields, combining, and `Set-Cookie`

`HttpHeaderValue` stores either a single string or several, so repeated field
lines are preserved without forcing a lossy early join; the comma-folded
`Value` is computed on demand for the common case where RFC 9110 §5.2 permits
combining a list-valued field into one line.

`Set-Cookie` is the field that must **never** be folded: each cookie occupies
its own field line (RFC 9110 §5.3, RFC 6265 §3), and HTTP/2 / HTTP/3 likewise
keep each `Set-Cookie` distinct. The cookie *request* header, conversely, is
coalesced with `"; "` (not `","`) when split across HTTP/2 field lines
(RFC 9113 §8.2.3). These are not ad-hoc checks scattered through the
transports — they are stated once in `HttpFieldRules`.

### `HttpFieldRules` — one source of truth for field classification

`HttpFieldRules` is the version-neutral statement of which fields are special:

- `IsConnectionSpecific` — `Connection`, `Proxy-Connection`, `Keep-Alive`,
  `Transfer-Encoding`, `Upgrade` (RFC 9110 §7.6.1). These apply to one
  connection and must not cross a version boundary; HTTP/2 and HTTP/3 treat
  their presence as malformed.
- `IsSingleton` — fields that must appear at most once because combining them
  changes meaning (`Content-Length`, `Host`, `Content-Type`, …).
- `IsSetCookie` / `ProhibitsCombining` — the no-fold rule.
- `IsProhibitedInTrailers` — the RFC 9110 §6.5.1 exclusion set (framing,
  routing, request modifiers, authentication, content-processing controls, and
  the `Trailer` field itself). A transport draining `response.Trailers` (or a
  caller staging them) checks this so framing/routing fields like
  `Content-Length` never leak into a trailer section.

Keeping these rules in one place is what lets the cross-version normalization
layer (the `.11` work) translate fields between HTTP/1.1, HTTP/2, and HTTP/3
deterministically instead of re-encoding the quirks at every call site.

### AOT posture

No reflection, no runtime code generation. Classification is a set of
case-insensitive `HashSet<string>` lookups; the trailer carriers are plain
dictionaries. Fully AOT/trim safe.

### Non-goals

- **Trailer emission / per-version surfacing.** The core models the trailer
  collection; whether a given transport surfaces or emits it is the transport's
  concern, and `IsSupported` reports the truth per exchange. The HTTP/1.1
  transport surfaces inbound request trailers (chunked) today; HTTP/2 / HTTP/3
  trailing-HEADERS surfacing and HTTP/1.1 outbound chunked-trailer emission are
  wired incrementally by the version transports — the model makes each a
  drop-in (`IsSupported = true` + a populated `HttpTrailerCollection`).
- **Per-field parsers.** `HttpFieldRules` classifies field *names*; it does not
  parse field *values* (dates, cache-control directives, etc.). Value parsing
  belongs to the field-specific consumer.

## Cross-version normalization

`HttpFieldNormalization` is the operational layer over `HttpFieldRules`: where
`HttpFieldRules` classifies field *names*, `HttpFieldNormalization` performs the
*translation operations* a transport applies while turning a wire field section
into an `IHttpRequest` / `IHttpResponse`. It exists so HTTP/1.1, HTTP/2, and
HTTP/3 normalize the shared concepts identically instead of each re-encoding the
quirks.

### What it centralizes

- **Authority resolution** (`ResolveAuthority`) — an explicit authority (the
  HTTP/2 / HTTP/3 `:authority` pseudo-header, or the HTTP/1.1 absolute-form
  target authority) supersedes the `Host` header, then falls back to `Host`,
  then to `HttpHost.Empty` (RFC 9112 §3.2.2, RFC 9113 §8.3.1, RFC 9114 §4.3.1).
  HTTP/2 (`Http2Stream`) and HTTP/3 (`Http3HeaderCodec`) both call it, so a
  request with `:authority` set and a conflicting `Host` resolves the same way
  on both.
- **Connection-specific rejection** (`IsForbiddenInHttp2Or3` +
  `IsTeValueValidInHttp2Or3`) — `Connection`, `Proxy-Connection`, `Keep-Alive`,
  `Transfer-Encoding`, and `Upgrade` are forbidden in HTTP/2 and HTTP/3, and
  `TE` may only be `trailers` (RFC 9113 §8.2.2, RFC 9114 §4.2). The HTTP/2 HPACK
  decoder and the HTTP/3 codec share this rule — closing a real gap where the
  HTTP/3 path previously did not reject these fields.
- **Repeated-field combining** (`CombineFieldValue`) — the request `Cookie`
  field coalesces with `"; "` (RFC 9113 §8.2.3, RFC 9114 §4.2.1); `Set-Cookie`
  is never folded; other list fields combine as distinct values. Previously the
  HTTP/3 path combined cookies with a comma; now it matches HTTP/2.

### Version-specific boundaries that must NOT cross

The normalization layer is deliberately the *only* place these
version-spanning rules live, but some behaviors are intentionally version-local
and must not be normalized away:

- **Framing fields** (`Transfer-Encoding`, `Content-Length`, `Connection`,
  `Keep-Alive`) are HTTP/1.1 connection mechanics. They are rejected — not
  translated — when they appear in HTTP/2 / HTTP/3, because their semantics do
  not exist there.
- **Pseudo-headers** (`:method`, `:scheme`, `:path`, `:authority`) are an
  HTTP/2 / HTTP/3 concept; they are reconciled into the version-neutral
  request shape (method, scheme, path, host) at decode time and never emitted
  as ordinary fields on the HTTP/1.1 side.

### AOT posture

Pure logic over the existing collections — no reflection, no codegen. Fully
AOT/trim safe.

## Request-parse interception seam

### What it is

`IHttpRequestInterceptor` + `HttpRequestInterceptorContext` +
`HttpRequestRejectedException` form the server-side seam that lets feature
packages participate while a request is being **parsed** — before it is
dispatched — without the server transport referencing any feature package:

- `OnRequestHead(context)` runs after the head is parsed and before the body is
  surfaced: attach typed features, adjust the body-size knob, or throw the
  typed rejection.
- `Stream OnRequestBody(context, body)` runs after the body stream is
  materialized: return the stream unchanged or a wrapper (read-only decorators,
  digest hashing, decompression). Wrappers own what they wrap.

Both members are default-implemented, following this library's recorded
"interface evolution via a default member" practice — implementers override
only the hook they need, and future hook points (a trailer hook is the known
candidate; an async variant is the known escape hatch) are added the same way
without breaking anyone.

### Why the seam is core and the features are not

This is the same taxonomy as `IHttpFeature` vs. the feature packages: **seams
live in core, features live in packages**. The interceptor contract is generic
infrastructure every parse-time feature shares (`Http.RequestLimits` today;
digest fields and request decompression are the designed next consumers), so it
sits here; the concrete `IHttpMaxRequestBodySizeFeature` was deliberately moved
*out* of this core into `Assimalign.Cohesion.Http.RequestLimits`. Registration
is transport-owned (`HttpConnectionListenerOptions.Interceptors` in
`Http.Connections`) because *when* hooks run is a transport decision; *what*
they can do is defined here.

Unlike the ExtendedConnect Items-key bridge (one-way, post-parse, no shared
symbol), this seam is a compile-time contract — justified specifically by
mutation the transport must enforce mid-parse, pre-dispatch feature attachment,
and stream replacement, none of which a loosely-typed key can express. New
capabilities that only need one-way post-parse publication should still prefer
the Items-key bridge.

### Contract details that are load-bearing

- **`Headers` is a read-only view** (`HttpHeaderCollection.AsReadOnly()`, a
  shared-store view with fail-loud mutation). The transport derives framing,
  keep-alive, and host resolution from the same collection after hooks run;
  a mutable view would be a self-inflicted request-smuggling primitive.
- **The context is the body-size cell.** `MaxRequestBodySize` (validated,
  `null` = unbounded) is the value the transport enforces;
  `FreezeMaxRequestBodySize()` / `IsMaxRequestBodySizeReadOnly` are the
  transport-owned freeze. Write-through features project this knob rather than
  copying it, so when the streaming-body rework moves the freeze from
  buffered-materialization to first-byte-read, no feature contract changes.
  The transport keeps the context alive until the body is consumed — the
  documented lifetime is "until the request body is consumed or the exchange
  completes", not "until dispatch".
- **Interceptor instances are shared across all connections/requests** and must
  be stateless; per-request state goes in `Features`. Hooks are CPU-only.
- **Rejection is typed.** `HttpRequestRejectedException` carries a 4xx/5xx
  status the transport answers before closing. It exists because an untyped
  `IOException`/`InvalidDataException` thrown from a hook is indistinguishable
  from a wire failure and would be silently swallowed by the transport's
  failure classifier.
- **Evolution rules:** v1 context members are `required` (init-only, except the
  deliberately-mutable body-size knob); all future members must be optional
  with defaults so existing construction sites (including test fakes — the
  context is deliberately constructible in tests) keep compiling.

### AOT posture

Interface dispatch plus the existing dictionary-backed feature lookup — no
reflection, no codegen.
