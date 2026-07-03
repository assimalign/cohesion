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
