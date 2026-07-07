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
  belongs to the field-specific consumer, and the shared toolkit those consumers
  build on for RFC 9651 syntax is the structured-fields surface documented below.
  `HttpFieldRules` stays name-classification only.

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

## Media types and content negotiation

The core owns the RFC 9110 §8.3 / §12 content-negotiation primitives so that every
consumer that must reason about representations — result writers and negotiation
(#149), static-file serving (#777), request/response compression (#779), and the
RFC 10008 QUERY content rules (#746) — shares one media-type value object and one
negotiation algorithm instead of each re-deriving Accept parsing. This is a
deliberate **fan-out foundation**: the surface is kept conservative because these
types are hard to change once several areas import them.

### `HttpMediaType` — the value object

`HttpMediaType` is a `readonly struct` that parses a `type/subtype` pair with
optional parameters, mirroring the span-based `TryParse`/`Parse` shape of the other
core value objects (`HttpMethod`, `HttpRequestTarget`). It carries four things the
negotiation layer needs:

- **Structured-syntax suffix.** The subtype is stored whole (`vnd.api+json`), and
  `Suffix` exposes the part after the last `+` (`json`) so a writer keyed on "any
  `+json` representation" does not re-split the subtype.
- **Parameters, minus `q`.** Parameters (`charset`, `boundary`, …) are retained as
  `HttpMediaTypeParameter` values; `Charset` is a convenience accessor. The Accept
  `q` weight is **not** a media-type parameter — it belongs to the Accept grammar
  and is stripped by `HttpAcceptParser` before the media type is built, so it never
  leaks into `Parameters`.
- **Wildcards + specificity.** A media range may wildcard the type (`*/*`) or the
  subtype (`text/*`). `Specificity` collapses the RFC 9110 §12.5.1 precedence into a
  single integer (`*/*` < `type/*` < `type/subtype` < each added parameter), which is
  what both the parser's ordering and the selector's "most specific matching range"
  rule compare on.
- **Directional matching.** `Includes(candidate)` treats the instance as a range and
  tests membership: type/subtype match respecting wildcards (case-insensitively), and
  every parameter the range constrains must be present on the candidate with an equal
  value (extra candidate parameters are allowed). Comparison is case-insensitive on
  parameter values too, which is pragmatic (charset is genuinely case-insensitive) and
  matches the behavior downstream consumers expect.

`HttpQuality` is a companion `readonly struct` storing the weight as an integer number
of thousandths (0–1000). Fixed-point storage makes equality and ordering exact — there
is no floating-point drift when comparing `q=0.7` against `q=0.700` — and keeps the
RFC 9110 §12.4.2 grammar (at most three fractional digits, range 0–1) enforceable in
`TryParse` without rounding heuristics.

### Why static parsers/selectors, not interfaces

`HttpAcceptParser` and `HttpContentNegotiation` are `static` helper classes, following
`HttpFieldRules`/`HttpFieldNormalization` rather than the interface-first pattern used
for services. They are pure, stateless functions over value types with exactly one
correct implementation — an interface seam would add indirection and an AOT-hostile
virtual dispatch for no substitutability benefit. The interface-first rule targets
injectable behavior; these are value transformations.

### Parsing is tolerant; selection is authoritative

`HttpAcceptParser` splits the four Accept-family headers **quote-aware** (a `,` or `;`
inside a quoted parameter value does not split an entry) and **skips** any malformed
comma-separated segment — a bad token, a missing `type/subtype`, or an unparseable `q`
— rather than throwing, so one broken entry never discards the whole header. The
returned lists are ordered by client preference: descending quality, and for equal
quality the more specific candidate first (specificity breaks q ties).

The preference-ordered list is a convenience, not the negotiation result. The
authoritative algorithm lives in `HttpContentNegotiation`: for each server
representation the quality is taken from the **most specific matching client range**
(RFC 9110 §12.5.1), the highest-quality representation wins, and ties are broken in
favor of the server's own preference order (the order it lists its options). A missing
Accept header means "accept everything" → the server's first option. Nothing acceptable
(all weights zero or unmatched) returns `false` — the signal a caller turns into
`406 Not Acceptable`; the core does not decide between 406 and a server default.

### The `identity` rule for content-coding selection

`TrySelectEncoding` centralizes the RFC 9110 §12.5.3 `identity` handling that
compression (#779) would otherwise get subtly wrong. `identity` (send the response
uncompressed) is **acceptable by default** and is refused only by an explicit
`identity;q=0` or a `*;q=0` with no overriding identity entry. When a listed coding is
acceptable the selector compresses even at a low weight, choosing `identity` over a
coding only when the client **explicitly** ranks identity strictly higher — which
matches real-world compression behavior while still honoring an explicit
no-compression preference. When no Accept-Encoding header is present the selector
returns `identity` (do not compress for a client that never advertised support).

### `HttpContentTypes` — the extension map

The extension-to-content-type table is a `static FrozenDictionary<string,string>`
built once at startup with case-insensitive keys and no reflection. `FrozenDictionary`
(not a plain `Dictionary` or a reflection-scanned MIME registry) is the AOT-safe choice
for a read-mostly lookup that is hot on the static-file path. Resolution matches the
**final** extension of a file name (`archive.tar.gz` → `.gz`). The table covers common
web asset types rather than the full IANA registry; consumers that need custom mappings
build their own overlay with `CreateMap`, which clones the defaults and applies
overrides — the default table is immutable and shared.

### AOT posture

Every primitive is span-based, allocation-conscious, and free of reflection or runtime
codegen. `FrozenDictionary` is trim/AOT-safe; parameter storage is a plain array (no
`ImmutableArray` dependency). Fully AOT/trim safe.

### Non-goals

- **Field wiring.** These are value objects and pure functions; reading `Accept` off a
  request, writing `Content-Type`/`Content-Encoding`/`Vary` onto a response, and the
  `406`-vs-default policy belong to the result-writer and middleware layers that consume
  them (#149, #777, #779), not to the core.
- **Charset/language sophistication.** `TrySelectByQuality` does exact-token-plus-`*`
  matching suitable for `Accept-Charset` and `Accept-Language`. It does **not** implement
  RFC 4647 language-range prefix matching (`en` matching `en-US`) — that is a routing/
  localization concern layered above this primitive if and when it is needed.
- **A complete MIME registry.** `HttpContentTypes` is a curated common-asset table, not
  the IANA database; breadth is added by consumer overlay, not by growing the core.
## Structured Field Values (RFC 9651)

The `StructuredField*` types are the core, field-agnostic toolkit for parsing and
serializing HTTP Structured Field Values (RFC 9651, which obsoletes RFC 8941 and adds the
Date and Display String bare types). They live in the core protocol library because they
are *protocol value objects* — the shared primitive that field-specific consumers build on,
not a transport concern.

### Why this is the shared primitive, and where it sits in the layering

Several current and planned fields are defined *as* structured fields: Priority
(RFC 9218), Digest / Content-Digest (RFC 9530), the QUERY method's Accept-Query
(RFC 10008), Proxy-Status (RFC 9209), and Signature-Input (RFC 9421). Each of those is a
thin *field-specific consumer* that says "this field is a Dictionary whose `u` key is an
Integer" — it should not re-implement item/list/dictionary tokenization. This toolkit is
the one place that tokenization lives, so the consumers only encode their field's shape.

This keeps the established layering intact:

- **`HttpFieldRules`** classifies field *names* (connection-specific, singleton,
  no-fold, trailer-prohibited). It says nothing about *values*.
- **The structured-fields toolkit** parses and serializes field *values* that use the
  RFC 9651 grammar. It is field-agnostic — it does not know that `Priority` exists.
- **Field-specific consumers** (future `Priority`, `Digest`, `AcceptQuery`, …) compose the
  two: they name their field via `HttpHeaderKey`, read the raw `HttpHeaderValue`, and hand
  it to `StructuredFieldDictionary.Parse` / `StructuredFieldList.Parse` /
  `StructuredFieldItem.Parse`, then interpret the typed result.

It deliberately does **not** live in `Http.Connections`: that is the wire/transport layer,
and DESIGN.md already assigns field-value parsing to consumers above the transport.

### The surface

Eight readonly-struct value objects model the RFC's data model exactly:

- `StructuredFieldBareItem` — the eight-way discriminated bare item (Integer, Decimal,
  String, Token, Byte Sequence, Boolean, Date, Display String), tagged by
  `StructuredFieldType`. Numeric kinds store inline; textual/binary kinds hold one
  reference. Factories (`FromInteger`, `FromToken`, …) validate range and syntax so an
  instance always serializes canonically; typed accessors (`AsInteger`, `AsToken`, …)
  throw `InvalidOperationException` on a type mismatch.
- `StructuredFieldParameters` — the ordered key→bare-item map attached to items, inner
  lists, and members (first-seen order preserved; a repeated key keeps its position and
  takes the last value).
- `StructuredFieldItem` — a bare item plus parameters; also the element type of an inner
  list, and the top-level `item` field type.
- `StructuredFieldInnerList` — a parenthesized sequence of items plus parameters.
- `StructuredFieldMember` — the `item`-or-`inner-list` union that a list holds and a
  dictionary maps to. Inspect `IsInnerList` before reading `Item` / `InnerList`.
- `StructuredFieldList` / `StructuredFieldDictionary` — the two collection-shaped top-level
  field types.

The three top-level types (`StructuredFieldItem`, `StructuredFieldList`,
`StructuredFieldDictionary`) each expose the same static entry points, matching the
existing `HttpRequestTarget` / `HttpHeaderValue` primitive style:

- `Parse(ReadOnlySpan<char>)` / `TryParse(ReadOnlySpan<char>, out …)` /
  `TryParse(ReadOnlySpan<char>, out …, out string? error)`
- `Parse(HttpHeaderValue)` / `TryParse(HttpHeaderValue, out …)` — the multi-line entry.
  A repeated field's lines are combined by comma via `HttpHeaderValue.Value` (RFC 9651
  §4.2), which is exactly the comma-combining the List/Dictionary grammar expects.
- `Serialize()` — the RFC 9651 §4.1 canonical form.

`Parse` throws `HttpException` (`HttpErrorCode.InvalidStructuredField`, via the internal
`HttpInvalidStructuredFieldException`); `TryParse` returns `false` with an optional
diagnostic. Why static `Parse`/`TryParse`/`Serialize` value types rather than an
interface-first `IStructuredField` hierarchy: these are immutable protocol primitives with
no polymorphic behavior to abstract — the same reasoning that makes `HttpHeaderKey` and
`HttpRequestTarget` structs. An interface would add allocation and indirection with no
extensibility payoff, and the surface is intentionally conservative because many consumers
will build on it and it is costly to change later.

### Strict fail-parsing

Parsing follows RFC 9651 §4.2 exactly: a single forward pass with **no lenient recovery**.
Any malformed token, out-of-range Integer/Date (±999,999,999,999,999), over-long Decimal
(>12 integer or >3 fractional digits), non-canonical Byte Sequence base64, or trailing
content fails the *entire* field. This is what lets a consumer treat a successful parse as
a fully validated value.

### AOT posture

No reflection, no runtime codegen, no dynamic serialization — a parser built from span
slicing and BCL numeric/base64/UTF-8 primitives. Tokenization is span-based: the parser
scans by index and materializes only the values it returns (the string of a String/Token,
the `byte[]` of a Byte Sequence, the result collections), with a fast path that slices
escape-free strings directly. Builds clean under the trim/AOT analyzers
(`IsAotCompatible=true`).

### Non-goals

- **Field-specific semantics.** This toolkit models the RFC 9651 *syntax*; it does not know
  any concrete field's schema (that `Priority.u` is 0–7, that `Content-Digest` values are
  Byte Sequences). Those rules belong to each field-specific consumer.
- **Retaining non-canonical input.** Parsing yields the RFC data model, not the original
  bytes; `Serialize()` always emits the §4.1 canonical form. A parse→serialize round-trip
  therefore normalizes (e.g. `1.50` → `1.5`, `-0` → `0`) rather than preserving the wire
  spelling.
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

## The response interceptor seam

`IHttpResponseInterceptor` (+ `HttpResponseInterceptorContext`) is the symmetric
counterpart to `IHttpRequestInterceptor`: a generic hook the server transport
invokes while an exchange's response pipeline is being set up, before the handler
runs. It exists so **response-side capabilities stay out of both the protocol core
and the transport**. Incremental response streaming — and Server-Sent Events on top
of it — is the first consumer, and neither the core nor the transport carries any
streaming/SSE type.

### Why generic, and how a feature plugs in

The request side already established the pattern: `Http.RequestLimits` participates
in request parsing via an `IHttpRequestInterceptor` (registered on the listener
options), and the transport enforces it without ever referencing that package. The
response side mirrors it exactly:

- The transport exposes its per-protocol **raw response body sink** as a plain
  `System.IO.Stream` on `HttpResponseInterceptorContext.ResponseBody`. That sink
  frames each write (HTTP/1.1 chunked, HTTP/2 / HTTP/3 `DATA` frames with
  flow-control backpressure), commits the head on the first write/flush, and is
  finalized by the transport when the exchange completes.
- A feature package (`Assimalign.Cohesion.Http.Streaming`) ships an
  `IHttpResponseInterceptor` that wraps that sink in a typed
  `IHttpResponseStreamingFeature` and installs it on `context.Features`. A handler
  resolves the feature (`context.Response.Streaming`) and writes.

So the streaming write/flush API, its state machine, and the SSE wire format all
live in feature packages; the core owns only the generic interceptor seam, and the
transport owns only the framing. The one streaming-adjacent thing that stays in the
core is the well-known **header-name constant** `HttpHeaderKey.LastEventId`,
alongside the other feature-specific header names already centralized there
(`Sec-WebSocket-*`, `Grpc-*`).

### Header-commit timing

Because the sink commits the head on the first write or flush, the status line and
headers are committed exactly once and locked thereafter — a rule the streaming
feature surfaces to callers and the SSE package relies on (set `Content-Type:
text/event-stream` before the first write). The interceptor runs *before* any
response byte is produced, so an interceptor may still set default response headers
on `HttpResponseInterceptorContext.Headers`.

### AOT posture

Interface dispatch over a snapshotted interceptor array; no reflection, no codegen.
