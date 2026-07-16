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
  The typed consumers for the timestamp, caching, and validator fields
  (`Date`/`Last-Modified`, `Cache-Control`, `ETag`, and the conditional-request
  fields) are documented under **HTTP caching and validators** below.
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

## Host values and allowlist matching

`HttpHost` carries the request's effective authority exactly as the transport
resolved it (`ResolveAuthority` above). On top of that raw value it exposes the
split into normalized host and port components, and `HttpHostMatcher` compiles
a host **allowlist** from patterns. The enforcement middleware lives in the
`Web.HostFiltering` feature package (issue #781); the matching primitives live
here per the typed-protocol-primitives rule — like the caching and range
toolkits, they are value logic every consumer must agree on, not Web behavior.

**Why an abstract class, not an interface.** `HttpHostMatcher` is a public
abstract class carrying its factories (`Create`, `MatchAny`) as static members,
with the compiled pattern matcher as the internal default implementation —
rather than the repo-default `IHttpHostMatcher` + static factory pair. Owner
decision (2026-07-16): one extensible concrete contract reads better than an
interface-per-concept for a primitive with exactly one meaningful default
implementation, and the base class gives derived matchers guided defaults
(`IsMatchAny => false`) an interface cannot. Precedent: the Dns area's
abstract-class contracts (contrast with FileSystem's interfaces). The deviation
is scoped to this type and marked at the declaration.

### The component split (`TryGetComponents`, `Host`, `Port`)

`TryGetComponents` is a span-based structural split of `host[:port]`, exposed
alongside allocating `Host` / `Port` convenience properties:

- A bracketed IPv6 literal is exposed **without** its brackets, so `[::1]` and
  `::1` yield the same component (bracket-insensitive comparison falls out for
  free).
- An unbracketed value containing multiple colons is treated as an IPv6 literal
  tolerated bracket-insensitively — such a value cannot carry a port.
- Malformed shapes return `false`: an unterminated/empty bracket form, junk
  after the closing bracket, a trailing `host:` with no digits, or a port that
  is not a decimal 1–65535. `Host` falls back to the raw value in that case.
- The split is structural, not semantic: host characters are not validated
  against the `reg-name` grammar and IPv6 contents are not parsed as addresses.

**Parity with routing.** The semantics deliberately mirror the Web routing
host-constraint (`RouteHostConstraint`, issue #788): the same bracket rules,
the same wildcard grammar, the same 1–65535 port range. One deliberate
strictness delta: the split validates port digits as part of parsing, where the
routing constraint's raw-text match tolerates junk port text on a route that
does not constrain the port. Selection can afford leniency; a validation
primitive claiming "components" cannot.

### The allowlist matcher

`HttpHostMatcher.Create` compiles patterns once into an immutable matcher:

- **Grammar:** `*` (match any), an exact host (name, IPv4, or IPv6 literal in
  either bracket form), or a wildcard subdomain `*.example.com` — any depth,
  apex excluded. The stored wildcard suffix keeps its leading dot, which is
  what enforces the label boundary (`evilexample.com` cannot match).
- **Precompiled:** normalized exact entries and wildcard suffixes in plain
  arrays; a request costs one component split plus ordinal-ignore-case span
  comparisons. No regex, no reflection, nothing per-request.
- **Fail-fast creation:** a pattern that carries a port, is malformed, or
  misuses `*` throws at `Create` — a pattern that could never match is a
  configuration error, not a runtime condition. An empty allowlist also throws
  (it would compile to deny-all); `*` beside a typo still validates the typo.
- **Match-any is honest:** `*` disables host checking entirely — even a
  malformed host passes. Structural strictness comes from real allowlists, and
  the empty-host policy belongs to the enforcement layer, not the matcher.

### Validation vs selection

The matcher answers *"is this host one of mine?"* — allowlist **validation**,
enforced as a 400 by the `Web.HostFiltering` middleware. Routing's host
constraints answer *"which endpoint serves this host?"* — **selection** (#788,
`RequireHost`). They share component semantics by design so a given wire value
means the same thing on both paths, but they are different questions: do not
fold one into the other. (A route host mismatch skips a candidate; an allowlist
mismatch rejects the request.)

### AOT posture

Pure span logic over strings — no reflection, no codegen. Fully AOT/trim safe.

### Non-goals

- **No DNS and no IDN/Punycode mapping** — comparison is against what the
  client actually sent on the wire.
- **No IPv6 address canonicalization** — `::1` and `0:0:0:0:0:0:0:1` are
  different patterns; bracket removal is the only normalization. Write the
  form clients send.
- **No port-aware allowlisting** — host validation is host-identity; which
  ports are served is a listener/binding concern. (Routing's host constraints
  *do* match ports, because selection legitimately needs them.)
- **No `reg-name` charset validation** — against a real allowlist, junk hosts
  fail to match anyway; the grammar check would add cost without adding
  security.

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

## Priority (RFC 9218 extensible priorities)

### The value object

`HttpPriority` is the field-specific consumer the Structured Field Values non-goals point at:
it projects the RFC 9218 *Priority Field Value* — the `u`/`i` structured-field dictionary
carried by the `Priority` request header **and** by HTTP/2 / HTTP/3 `PRIORITY_UPDATE` frames —
onto the two things a scheduler consumes: an **urgency** 0–7 (`u`, default 3) and an
**incremental** flag (`i`, default false). It is a `readonly struct` with `TryParse`, `Serialize`,
and value equality, matching the shape of the other core value objects (`HttpMediaType`,
`HttpQuality`).

Parsing goes **through** `StructuredFieldDictionary` rather than around it — there is one
structured-field parser in the stack, and `HttpPriority` only adds the field's semantics on top
of it. The header name is exposed as the typed `HttpHeaderKey.Priority`.

### Tolerant field semantics, strict syntax

The structured-field *syntax* is strict (a malformed dictionary fails `TryParse`, which returns
the default and `false`), but the *field members* are tolerant exactly as RFC 9218 §4 requires: an
absent, out-of-range (not 0–7), or wrong-typed `u` falls back to urgency 3; an absent or
non-boolean `i` falls back to non-incremental; and unrecognized members are ignored. This split
lets a transport treat a well-formed-but-nonsensical `Priority` value as "no signal" (the default)
without raising a protocol error, while still rejecting genuinely malformed structured fields.

### Why a value object here and the scheduler elsewhere

`HttpPriority` is pure parsing/representation and therefore lives in the protocol core (Lane B).
*Acting* on the priority — urgency-ordered, non-incremental-first response scheduling, the
`PRIORITY_UPDATE` frame engine, per-stream priority state — is wire behavior and lives in
`Assimalign.Cohesion.Http.Connections` (Lane A). The value object is the seam between the two: the
transport parses header and frame field values into `HttpPriority` and schedules on its
`Urgency`/`Incremental`.

### AOT posture

No reflection or dynamic serialization; parsing is span-based via the structured-field toolkit and
the ASCII→`char` bridge on the transport side is stack-allocated for the small field values that
occur in practice. Builds clean under the trim/AOT analyzers (`IsAotCompatible=true`).

## Alternative services (RFC 7838 `Alt-Svc`)

### The value object

`HttpAltService` is a `readonly struct` modeling one RFC 7838 §3 *alt-value*: an ALPN
`ProtocolId` bound to an alt-authority (`Host` — optional, `null` meaning "the request's own
host" — plus a required `Port`), with the caching parameters `MaxAgeSeconds` (`ma`) and `Persist`
(`persist=1`). It carries `Format`/`ToString` for the single alt-value, static
`FormatHeader`/`TryParseHeader` for the comma-separated list form and the special case-sensitive
`clear` token, and value equality — the same shape as the other core value objects
(`HttpMediaType`, `HttpPriority`, `HttpEntityTag`). The alt-authority is serialized as a
quoted-string (`h3=":443"`, `h2="alt.example.com:8000"`) so a host that contains a colon (an IPv6
literal) round-trips; the parser splits the authority at its last colon to keep the port.

### Why it lives in the protocol core, and who emits it

The type is pure parsing/representation with no transport dependency, so it sits in Lane B beside
the other field value objects. It is the discovery primitive for HTTP/3 (RFC 9114 §3.1): a server
that speaks h3 over QUIC alongside h1/h2 over TCP advertises the h3 endpoint by emitting an
`Alt-Svc` response header, and a TCP client learns the alternative from it. **Emission is the
transport's job**, not the core's — `Assimalign.Cohesion.Http.Connections` is the only component
that knows whether an h3 multiplexed listener is registered alongside the stream listeners, so it
owns the advertisement policy (opt-in, `ma`, explicit-authority override) and the header injection.
This core type only formats and parses the value; see the Http.Connections `DESIGN.md` for the
advertisement design. `ma`/`persist` are modeled as data for a future client-side alternative
cache; honoring a received `Alt-Svc` is a client concern this library does not implement.

### Tolerant parsing, strict port

Parsing skips OWS, ignores unrecognized parameters (RFC 7838 §3), and treats an empty alt-authority
host as same-host, but rejects a missing or non-numeric port and an unquoted alt-authority — a
malformed alt-value fails its `TryParse` rather than being silently coerced. `clear` is matched
case-sensitively per the RFC.

### AOT posture

No reflection or dynamic serialization; the format path uses a small `StringBuilder` and the parse
path is span-based. Builds clean under the trim/AOT analyzers (`IsAotCompatible=true`).

## The exchange interceptor seam

### One seam, one interface, one registration

`IHttpExchangeInterceptor` is the server transport's **single** extension seam
for participating in an exchange's lifecycle: one interface whose hooks span the
request phase (`AfterRequestHead` → `BeforeRequestBody` → `AfterRequestBody`)
and the response phase (`BeforeResponse` → `BeforeResponseHeadAsync` →
`AfterResponseAsync`), registered once on the transport's single
`HttpConnectionListenerOptions.Interceptors` list. A feature is one logical
unit with one registration — the earlier split into separate request/response
interceptor interfaces forced a package like `Http.ProtocolUpgrade` (one
stateless class participating in both phases) into two factories and two
registrations, a distinction users had to learn that encoded no real layer
boundary.

Three contract mechanics carry the design:

- **The guided abstract base is the implementation path.**
  `HttpExchangeInterceptor` implements the interface with virtual no-op
  members, so an implementation derives from it and overrides only the hooks it
  participates in. The base is the seam's compatibility surface: future hooks
  are added there as virtual no-ops (with matching interface members), so
  implementations built on it keep compiling. The interface itself carries
  plain members (no default implementations) — it is the contract the transport
  consumes; implementing it directly is permitted but opts out of the
  compatibility guarantee. This is the repo's recorded "interface-first with a
  guided abstract base" pattern.
- **`Scopes` declares interest.** `HttpInterceptorScopes` (Request / Response /
  All, default All on the base) tells the transport which phases to invoke the
  interceptor for. The transport snapshots the single list once and partitions
  it by scope, which is what keeps the zero-cost fast paths scope-exact: a
  request-only interceptor (e.g. `Http.RequestLimits`, default-installed by
  Web.Hosting) is never the reason a response sink and exchange control are
  constructed.
- **The sync/async split encodes pump safety.** The four parse-path hooks are
  `void` and must be CPU-only — on HTTP/2 they run on the connection's single
  frame pump, where a stalled hook stalls every multiplexed stream; the two
  send-path hooks are `ValueTask`-returning because awaiting is safe there.
  The asymmetric signatures are the constraint, stated in the type system.

### Where things live — the delineation rule

The seam sits inside a deliberate layering model, stated once here because every
"where does this feature go?" question resolves against it:

> **Mechanisms live at the lowest level that can observe what they need;
> decisions live at the application level. Where a thing registers tells you
> what it is.**

| Level | Owns | Extension surface |
|---|---|---|
| Connections | bytes, pipes, TLS | connection layers (`UseTls`) |
| Http.Connections | wire framing, protocol conformance, limits, flow control | this interceptor seam (+ the control's wire mechanisms) |
| Http.* feature packages | one capability each; the bridge from transport tap to app-facing `IHttpFeature` | implement `IHttpExchangeInterceptor`; install features |
| Web (application) | the pipeline and **all decisions** — cancel/abort, error responses, policies | middleware + `IHttpContext` |

Concretely: aborting an exchange is an application decision, so it has no seam
member — it is `IHttpContext.Cancel`/`CancelAsync`, which the transport honors
at its lifecycle checkpoints with version-appropriate wire behavior. A takeover
is a transport mechanism (`IHttpExchangeControl.TakeOver`) that a feature
package wraps (`context.Upgrade`) so the *application* decides when to exercise
it. Interceptors are wiring, not deciders: a parse-path hook that must refuse a
request throws the typed `HttpRequestRejectedException`; anything else it
cannot express is, by design, an application concern.

### The request phase

The request-phase hooks let feature packages participate while a request is
being **parsed** — before it is dispatched — without the server transport
referencing any feature package. In lifecycle order:

- `AfterRequestHead(context)` runs after the head is parsed and before the body
  is surfaced: attach typed features, adjust the body-size knob, or throw the
  typed rejection.
- `BeforeRequestBody(context)` runs after every head hook and the knob freeze,
  immediately before the transport reads (HTTP/1.1) or exposes (HTTP/2 / HTTP/3)
  the body — the knobs are read-only here; observe, attach, or reject. Skipped
  for CONNECT tunnels.
- `Stream AfterRequestBody(context, body)` runs after the body stream is
  materialized: return the stream unchanged or a wrapper (read-only decorators,
  digest hashing, decompression). Wrappers own what they wrap.

Defaults live on the `HttpExchangeInterceptor` base (virtual no-ops), so
implementers override only the hooks they need; future hook points (a trailer
hook is the known candidate) are added to the base the same way without
breaking anyone built on it.

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

## HTTP caching and validators (RFC 9111 / RFC 9110)

The core owns the typed primitives for the caching and validator fields:
`Cache-Control` directive parsing (RFC 9111 §5.2), the freshness arithmetic
(§4.2), the `ETag` value type and its strong/weak comparison (RFC 9110 §8.8.3),
HTTP-date parsing (§5.6.7), and conditional-request evaluation (§13). These are
the typed *field-value consumers* the `HttpFieldRules` "per-field parsers"
non-goal delegates to — the same relationship `HttpMediaType` and the
structured-fields toolkit already have to the core. They are **not** a cache:
there is no store, no revalidation transport, and no policy.

### Why the core, not a layered `Http.Caching` package

Two forces put these in the protocol core rather than in a sibling package
beside `Http.Sessions`/`Http.Forms`:

- **They parse their own values.** Like `HttpMethod`, `HttpStatusCode`, and
  `HttpMediaType`, an entity-tag or a `Cache-Control` field is a self-contained
  protocol value object. It does not extend `HttpFieldRules` (which stays
  name-classification only) or touch the header collections, so it does not
  cross the application-layer boundary the `HttpProtocolCoreBoundaryTests`
  guard — a client, proxy, or edge cache wants exactly these types without
  dragging in session/form semantics.
- **The validator types are shared, not duplicated.** The range-request and
  precondition primitives (RFC 9110 §13.2.2 / §14) land in this same core and
  need the identical `ETag`, `If-Match`/`If-None-Match`, and conditional-request
  machinery. A layered caching package would force a dependency inversion (core
  range code depending on a caching package) or a second copy of the entity-tag
  grammar. Keeping the shared validator surface in the core lets both consumers
  reference one implementation. `HttpEntityTag`, `HttpEntityTagCondition`, and
  the header preconditions live here; the range-specific `If-Range` (§13.2.2
  step 5) and range selection are layered above, reusing these types.

### The surface

- **`HttpEntityTag`** — a `readonly struct` holding the opaque tag content
  (without the surrounding quotes) and a weakness flag. It exposes the two
  RFC 9110 §8.8.3.2 comparison functions as distinct methods —
  `StrongEquals` (both tags strong and octet-equal) and `WeakEquals` (octet-equal
  regardless of weakness) — because the two are not interchangeable: caching and
  `If-None-Match` use weak comparison, while `If-Match`/`If-Range` use strong.
  `Equals`/`==` are stricter still (structural: content *and* weakness), so the
  type is a sound dictionary key; the comparison methods, not the operators,
  carry the HTTP semantics.
- **`HttpEntityTagCondition`** — the parsed `If-Match`/`If-None-Match` value:
  either the `*` wildcard or a comma-separated tag list. `MatchesStrong` /
  `MatchesWeak` fold the wildcard (matches when a representation exists) and the
  list (any member matches under the chosen comparison) into one call the
  evaluator and the range primitives share.
- **`HttpCacheControl`** — one `readonly struct` for both request and response
  directives (the field is bidirectional). Recognized directives are typed
  (`bool` flags packed into an internal `[Flags]` enum, delta-seconds as
  `TimeSpan`, the `no-cache`/`private` field-name arguments as string lists);
  unrecognized directives are preserved as `Extensions` (§5.2.3) so a
  parse→serialize round-trip is lossless. `max-stale` distinguishes
  present-without-value (accept any staleness) via `HasMaxStale` from a bounded
  value. Over-large delta-seconds clamp to ~68 years (2³¹−1 s) per §1.2.2.
- **`HttpDate`** — parses all three §5.6.7 forms (IMF-fixdate, RFC 850, asctime)
  via `DateTimeOffset.TryParseExact` with invariant culture, and formats the
  preferred IMF-fixdate. There is deliberately no throwing `Parse`: a malformed
  date is *ignored* by conditional-request rules (§13.1.3), so the `TryParse`
  shape is the correct one and the consumer maps failure to "field absent".
- **`HttpFreshness`** — pure §4.2 arithmetic: freshness-lifetime selection
  (§4.2.1), the current-age algorithm (§4.2.3), and the `IsFresh` comparison.
  Every timestamp is a parameter — no ambient clock is read — so the helpers are
  deterministic and unit-testable, and the *policy* (heuristic freshness §4.2.2,
  storage, revalidation) stays with the consuming cache.
- **`HttpConditionalRequest`** + **`HttpConditionalRequestContext`** +
  **`HttpPreconditionOutcome`** — the §13.2.2 evaluator over already-parsed
  inputs. It implements the four header preconditions in order (`If-Match`,
  then `If-Unmodified-Since` only when `If-Match` is absent; `If-None-Match`,
  then `If-Modified-Since` only when `If-None-Match` is absent and the method is
  a read). This encodes the two rules the feature calls out: `If-None-Match`
  takes precedence over `If-Modified-Since`, and a failed read precondition is
  `304` for the read methods but `412` otherwise. The read methods are GET and
  HEAD plus QUERY: RFC 10008 §2.6 requires a conditional QUERY to be evaluated
  exactly as the equivalent conditional GET (same selected representation, `304`
  where a GET would produce one), so the method classification lives here in the
  evaluator rather than being re-derived by each consumer. Resource existence
  (for the `*` wildcard) is inferred from a supplied validator, with an explicit
  `HasCurrentRepresentation` escape hatch for the rare validator-less resource.

### Why value objects and static helpers, not interfaces

`HttpFreshness` and `HttpConditionalRequest` are `static` (like `HttpFieldRules`,
`HttpFieldNormalization`, and `HttpContentNegotiation`), and the rest are value
objects with `TryParse`/`Parse`/`ToString` (like `HttpMediaType` and the
structured-field types). These are pure, stateless transforms over value types
with exactly one correct implementation; an interface seam would add allocation
and virtual dispatch with no substitutability payoff. The interface-first rule
targets injectable behavior — these are protocol value transformations.

### Parsing posture

`TryParse` never throws on malformed wire input and returns `false`; the paired
`Parse` throws a typed `HttpException` (`HttpErrorCode.InvalidCacheControl` /
`InvalidEntityTag`). `Cache-Control` parsing is a single span-based pass that
splits on unquoted commas, skips empty list elements (RFC 9110 §5.6.1.2), fails
on a non-token directive name or a malformed delta-seconds argument, and
tolerates a quoted delta-seconds some senders emit. The `no-cache`/`private`
field-list argument (uncommon) is the one place a materialized string split is
used rather than the span path; the entity-tag grammar validates `etagc`
(§8.8.3) character-by-character.

### Reading the fields is the consumer's job

As with the media-type primitives, the core does **not** read `Cache-Control`,
`ETag`, or the conditional fields off an `IHttpRequest`/`IHttpResponse`. A
consumer looks the field up by `HttpHeaderKey`, hands the raw value to the
relevant `TryParse`, and populates `HttpConditionalRequestContext`. Field wiring,
the `304`-vs-`412` response shaping, and the caching decision belong to the
response-caching middleware and the range/precondition layer that build on these
primitives.

### AOT posture

Span-based parsing, no reflection, no runtime codegen. `HttpDate` uses
`DateTimeOffset.TryParseExact` with `CultureInfo.InvariantCulture` (the invariant
month/day names match HTTP's fixed English tokens) — a data-driven parse with no
dynamic member access. Builds clean under the trim/AOT analyzers
(`IsAotCompatible=true`).

### Non-goals

- **A cache.** No response storage, cache-key derivation, revalidation transport,
  or heuristic-freshness *decision*. `HttpFreshness` supplies the arithmetic; the
  policy is the consumer's (a future server-side output cache, #795).
- **Field wiring and response shaping.** Reading the request/response fields and
  emitting `304`/`412`/`Vary` is the middleware's concern, not the core's.
- **Range requests.** Byte-range parsing, `206`/`416` selection, and the
  `If-Range` precondition are the range-request primitives' responsibility; they
  reuse `HttpEntityTag`, `HttpEntityTagCondition`, and the comparison helpers here
  rather than re-deriving them.
- **Cookie `Max-Age`.** That is RFC 6265, a different grammar owned by the cookie
  model; the delta-seconds handling here is `Cache-Control`-specific.

### The response phase

The response-phase hooks (+ `HttpExchangeInterceptorResponseContext`) run along an
exchange's response lifecycle. They exist so **response-side capabilities stay
out of both the protocol core and the transport**: incremental response
streaming (and Server-Sent Events on top of it), interim (`1xx`) responses, and
protocol upgrade / `CONNECT` takeover are the consumers, and neither the core
nor the transport carries any streaming/SSE/interim/upgrade type. In lifecycle
order:

- `BeforeResponse(context)` — exchange setup, before the handler runs: attach
  typed features, wrap the raw sink, capture the exchange control. Runs inline
  on the parse/dispatch path (on HTTP/2 the connection's frame pump), so it must
  be CPU-only.
- `ValueTask BeforeResponseHeadAsync(context, ct)` — fires exactly once,
  immediately before the final response head is committed (buffered send or
  streaming first-commit, whichever happens first). The last point to mutate
  status/headers or emit an interim response through the control; the transport
  re-reads the exchange's state after the hooks run, so a concurrent
  application cancel or a takeover is honored instead of writing the head. Runs
  on the send path, so awaiting is safe.
- `ValueTask AfterResponseAsync(context, ct)` — fires exactly once, after the
  final response is fully written (never for an aborted or taken-over
  exchange): access logging, metrics, digests.

Defaults live on the `HttpExchangeInterceptor` base (as on the request phase),
and the two send-path hooks are async by design — they are this seam's realized
"async escape hatch".

### Why generic, and how a feature plugs in

The request side already established the pattern: `Http.RequestLimits` participates
in request parsing via an `IHttpExchangeInterceptor` (registered on the listener
options), and the transport enforces it without ever referencing that package. The
response side mirrors it exactly, with **two transport-backed objects** on the
context instead of per-capability members:

- The transport exposes its per-protocol **raw response body sink** as a plain
  `System.IO.Stream` on `HttpExchangeInterceptorResponseContext.ResponseBody`. That sink
  frames each write (HTTP/1.1 chunked, HTTP/2 / HTTP/3 `DATA` frames with
  flow-control backpressure), commits the head on the first write/flush, and is
  finalized by the transport when the exchange completes.
- The transport exposes its per-exchange **exchange control** as an
  `IHttpExchangeControl` on `HttpExchangeInterceptorResponseContext.Control` — the
  single generic surface for the transport-owned wire mechanisms outside the
  normal response path: interim (`1xx`) writes (`CanWriteInterimResponse` /
  `WriteInterimResponseAsync`) and the raw-stream takeover (`CanTakeOver` /
  `TakeOver()`). One control deliberately replaces the former per-capability
  contracts (`IHttpConnectionTakeover`, `IHttpInterimResponseWriter`): a new
  wire mechanism composes from the hooks plus this control instead of adding a
  new core abstraction and new transport plumbing. The control carries
  mechanisms, not decisions — aborting is `IHttpContext.Cancel`, on the
  application surface.
- A feature package ships an `IHttpExchangeInterceptor` that wraps the sink or
  the control in a typed feature and installs it on `context.Features`:
  `Http.Streaming` wraps the sink in `IHttpResponseStreamingFeature`;
  `Http.InterimResponses` wraps the control's interim writes in
  `IHttpInterimResponseFeature`; `Http.ProtocolUpgrade` wraps the control's
  takeover in `IHttpProtocolUpgradeFeature`.

So the streaming write/flush API, its state machine, the SSE wire format, the
interim-response ergonomics, and all upgrade semantics live in feature packages;
the core owns only the generic seam (hooks + sink + control), and the transport
owns only the wire mechanics. The one streaming-adjacent thing that stays in the
core is the well-known **header-name constant** `HttpHeaderKey.LastEventId`,
alongside the other feature-specific header names already centralized there
(`Sec-WebSocket-*`, `Grpc-*`).

### Header-commit timing

Because the sink commits the head on the first write or flush, the status line and
headers are committed exactly once and locked thereafter — a rule the streaming
feature surfaces to callers and the SSE package relies on (set `Content-Type:
text/event-stream` before the first write). `BeforeResponse` runs before any
response byte is produced, so an interceptor may still set default response headers
on `HttpExchangeInterceptorResponseContext.Headers`; `BeforeResponseHeadAsync` is the last
word — it fires immediately before the commit on whichever path commits first.

### Exchange control semantics that are load-bearing

- **Capability probes are report-don't-throw.** `CanTakeOver` is `false` on
  HTTP/2 / HTTP/3 (multiplexed streams over a shared connection; those protocols
  removed `Upgrade`) and once the exchange can no longer be handed off;
  `CanWriteInterimResponse` flips to `false` once the final head is committed.
  Feature packages degrade (e.g. `context.Upgrade == null`) rather than surface
  a feature whose action could never work.
- **`TakeOver()` is one-shot** and claims the connection *before* any transition
  byte is written, so two features can never fight over the same connection and
  a failed accept can never be followed by a second HTTP response on a
  desynchronized stream. From that instant the transport suppresses its own
  response and stops reusing the connection.
- **An application cancel (`IHttpContext.Cancel`) is honored at the transport's
  checkpoints** with the version's wire behavior (h1 writes no response and
  ends the connection after the exchange, h2 `RST_STREAM(CANCEL)`, h3 stream
  abort), and the control's probes observe it: a cancelled exchange reports
  `CanWriteInterimResponse == false` and `CanTakeOver == false`.
- **`Control` is optional** (`null` in hand-built test contexts).

### AOT posture

Interface dispatch over a snapshotted interceptor array; the control is one small
per-exchange object; no reflection, no codegen.

## Range requests and the `If-Range` precondition (RFC 9110 §14 / §13.1.5)

Range requests are the layer the caching/validator section above explicitly
deferred: it owns `ETag`, `HttpEntityTag`, `HttpEntityTagCondition`, `HttpDate`,
and the four-step §13.2.2 precondition evaluator (`HttpConditionalRequest`); this
section adds byte-range parsing, `Content-Range`, the `If-Range` value object,
`206`/`416` selection, and the §13.2.2 **step-5** range-application decision that
*reuses* those primitives rather than re-deriving them. `Web.StaticFiles` (#777)
and output caching (#795) consume this layer.

### The type map, and what is reused vs. new

| Concern | Type(s) | Owner |
|---|---|---|
| Entity-tag, `If-Match`/`If-None-Match` value, HTTP-date, §13.2.2 steps 1–4 | `HttpEntityTag`, `HttpEntityTagCondition`, `HttpDate`, `HttpConditionalRequest`, `HttpPreconditionOutcome` | caching/validator section (#755) |
| `Range` request header | `HttpRange` (one spec), `HttpRangeHeader` (the set) — RFC 9110 §14.1.1 | this section |
| `Content-Range` response header | `HttpContentRange` — §14.4 | this section |
| `If-Range` (entity-tag or date) + its step-5 decision | `HttpIfRange` (+ `Matches`) — §13.1.5 | this section |
| `206`/`416` selection | `HttpRangeSelector` → `HttpRangeSelection` / `HttpRangeSlice` / `HttpRangeSelectionStatus` — §14.2 | this section |

The range value objects are `readonly struct`s with span `TryParse`/`Parse`/
`ToString`, and `HttpRangeSelector` is a `static` class — the same "value objects
+ static protocol transforms, not interfaces" rationale documented in the
media-type and caching sections. No new ETag or condition type is introduced:
`HttpIfRange`'s entity-tag form is an `HttpEntityTag`, and its `Matches` reuses
`HttpEntityTag.StrongEquals`.

### Parsing is strict, and "invalid" ≠ "unsatisfiable"

A `Range` header yields one of three downstream outcomes, and separating them is
deliberate:

1. **Unparseable / unknown unit → ignore the range, serve `200`.**
   `HttpRangeHeader.TryParse` returns `false` for a non-`bytes` unit or any
   syntactically invalid range-set (one bad member fails the *whole* set) — the
   RFC 9110 §14.2 "a server MAY ignore a Range it can't or won't honor." The
   selector is never called.
2. **Valid but wholly out of range → `416`.** A parsed `bytes` set where no spec
   overlaps the representation is *unsatisfiable* — a different response (`416`
   with `Content-Range: bytes */N`), not an ignore.
3. **Valid, ≥1 overlapping → `206`.**

Collapsing (1) and (2) — e.g. `416` for a malformed header — is a common and
wrong implementation; keeping strict parse (`false`) separate from unsatisfiable
selection (`Unsatisfiable`) is what makes the three-way outcome representable.

### Range selection: 206 / 416 / Full, order preserved, DoS-guarded

`HttpRangeSelector.Select(range, completeLength, maxRanges)` resolves each spec
via `HttpRange.TryResolve` (§14.1.2: open-ended and suffix ranges clamp to the
content; a `first-pos` at/after the end, an empty `-0` suffix, or a zero-length
representation are unsatisfiable) and returns `HttpRangeSelection`:

- **`Partial`** — one `HttpRangeSlice` per satisfiable spec, in client order, each
  carrying the `Content-Range` a `206` (single or `multipart/byteranges`)
  advertises. Ranges are **not coalesced or reordered** — the RFC permits
  coalescing but does not require it; preserving client order keeps the primitive
  predictable, and a consumer can coalesce the returned slices.
- **`Unsatisfiable`** — carries `UnsatisfiedContentRange` = `bytes */N`.
- **`Full`** — the escape hatch for a range set larger than `maxRanges`
  (default 16). RFC 9110 §14.2 blesses ignoring "egregious" range requests; a huge
  set of tiny overlapping ranges is the classic amplification vector, so the count
  cap degrades to a plain `200`. It is a policy knob, surfaced as a parameter.

### `If-Range` is step 5, and composes with the shared evaluator

`HttpConditionalRequest.Evaluate` (the caching/validator section) resolves
§13.2.2 steps 1–4 and returns `Proceed` / `NotModified` / `PreconditionFailed`.
It deliberately stops there: `If-Range` is range-specific. `HttpIfRange.Matches`
is step 5, and the two compose into the full "typed decision" the range feature
needs:

```
outcome = HttpConditionalRequest.Evaluate(context)      // steps 1–4 → 304 / 412 / proceed
if outcome != Proceed              → send 304 / 412
else if a Range header is present:
    applyRange = ifRange is null  ||  ifRange.Matches(currentETag, currentLastModified)   // step 5
    if applyRange   → HttpRangeSelector.Select(...)     → 206 / 416 / (Full → 200)
    else            → 200 full          // validator stale: give the client the whole thing
else                               → 200 full
```

Two correctness details:

- **`If-Range` is always strong.** The entity-tag form uses
  `HttpEntityTag.StrongEquals` (a weak or absent current tag never applies the
  range); the date form applies the range only when the representation has not
  been modified after the client's date (`Last-Modified ≤ If-Range date`, the
  Kestrel rule). A mismatch *ignores* the range (full `200`) — `If-Range` is
  "give me the whole thing if my copy is stale", never a `412`.
- **One-second granularity.** `HttpIfRange.Matches` truncates the representation's
  `Last-Modified` to whole seconds before the date comparison, because an
  HTTP-date carries only whole seconds; a sub-second write therefore does not read
  as "modified".

### AOT posture (range/If-Range)

Structs, spans, `long.TryParse`, and reuse of `HttpDate`/`HttpEntityTag` — no
reflection, no runtime codegen, no dynamic dispatch. Trim-safe by construction
(`IsAotCompatible=true`).

### Non-goals (this layer)

- **No `multipart/byteranges` rendering.** The selector returns slices and their
  `Content-Range`s; composing the multipart body (boundaries, part headers) is a
  response-writer concern (#769 streaming path / `Web.StaticFiles` #777).
- **No range coalescing** and **no `Accept-Ranges` emission** — server response
  concerns, not value-object ones.
- **No re-derived validator types.** ETag, the condition list, HTTP-date, and the
  step-1–4 evaluator belong to the caching/validator section; this layer reuses
  them.
- **No header-collection integration.** As with the media-type and caching
  primitives, reading the `Range` / `If-Range` / conditional fields off an
  `IHttpRequest` and populating `HttpConditionalRequestContext` is the consuming
  middleware's job.

## Forwarding headers (RFC 7239 `Forwarded` + `X-Forwarded-*`)

The core owns the value objects that parse and serialize the proxy-forwarding
headers — the RFC 7239 `Forwarded` element list and the de-facto
`X-Forwarded-For` / `X-Forwarded-Proto` / `X-Forwarded-Host` list forms. Every
Cohesion web service is deployed behind at least one proxy hop (the
ApplicationModel K8s self-registry gateway, LoadBalancer/NatGateway resources),
so without these primitives every downstream concern — CORS, cookie `Secure`
decisions, session partitioning, rate-limit keys, access logging — sees the
proxy's IP and `http://` scheme instead of the client's.

### The protocol half vs. the trust half

This library is deliberately the **protocol half only**: it turns raw header
text into typed, validated value objects and back. It contains **no trust
model**. Deciding *which* forwarded hops to believe — `KnownProxies`,
`KnownNetworks`, `ForwardLimit`, header selection — lives in the
forwarded-headers middleware (#778) in `Assimalign.Cohesion.Web`, because that
is a policy decision that depends on the deployment topology, not on the wire
grammar. Keeping the split here means the security-sensitive code has one job
(apply policy to already-parsed, already-validated data) and the parser has one
job (be a correct, total function over hostile input). This mirrors the
established seam/feature taxonomy: protocol value objects in core, policy in
the layer that composes them.

### The effective-identity contract lives in `Http.Forwarded`, not here

The *output* contract of that trust evaluation — `IHttpForwardedFeature`
(effective scheme/host/remote endpoint, the original wire values, the
accepted-hop count) and the feature-first `Effective*` read convention on
`IHttpContext` — ships as its own layered package,
`Assimalign.Cohesion.Http.Forwarded`, following the same separation that keeps
`Http.Sessions`/`Http.Cookies`/`Http.Forms` out of the core: per-concern
feature contracts are opt-in assemblies extending the `Assimalign.Cohesion.Http`
namespace, and the core's surface stays protocol-only. That package's
`docs/DESIGN.md` records the read-convention decision (feature-first with wire
fallback; the get-only wire surfaces are never mutated); its producer is the
`Assimalign.Cohesion.Web.ForwardedHeaders` middleware.

### The surface

Five readonly-struct value objects, in the span-based `TryParse`/`Parse`/
`Serialize` style of the other core primitives (`HttpMediaType`,
`HttpRequestTarget`, the `StructuredField*` family):

- **`HttpForwardedNode`** — an RFC 7239 §6 `node` identifier (the `for`/`by`
  value): `nodename [ ":" node-port ]`. It classifies the nodename as an IPv4
  literal, a bracketed IPv6 literal, `unknown`, or an obfuscated identifier
  (`_`-prefixed, §6.3), and the port as numeric or obfuscated (§6.4). `Address`
  and `PortNumber` give the typed projections; `Name`/`Port` preserve the exact
  spelling so `ToString` round-trips.
- **`HttpForwardedParameter`** — one `forwarded-pair` (`token "=" value`),
  carrying registered *and* extension parameters so an element round-trips
  losslessly.
- **`HttpForwardedElement`** — one `forwarded-element` (one proxy hop): typed
  `For`/`By`/`Host`/`Proto` accessors over an ordered parameter list, plus
  `TryGetParameter` for extensions and a typed `Create` factory for the
  proxy-*writing* path.
- **`HttpForwardedElementCollection`** — the whole `Forwarded` header
  (`1#forwarded-element`).
- **`HttpForwardedValues`** — the ordered entry list of any one `X-Forwarded-*`
  header.

`HttpHeaderKey` gained `Forwarded`, `XForwardedFor`, `XForwardedHost`, and
`XForwardedProto` alongside the existing keys.

### RFC 7239 strictness vs. `X-Forwarded-*` pragmatism

`HttpForwardedNode` is reused across both header families, so it accepts a
**bare** IPv6 literal (`2001:db8::1`) in addition to the RFC-mandated bracketed
form (`[2001:db8::1]`) — bracket-less IPv6 is what real proxies write into
`X-Forwarded-For`. Because a bare IPv6 literal's own colons cannot be
distinguished from a trailing `:port`, a bare literal is always taken as the
whole nodename with no port (which is exactly why RFC 7239 mandates brackets when
a port is needed). IPv4/IPv6 recognition delegates to `System.Net.IPAddress`, so
the accepted address forms are precisely the BCL's.

`HttpForwardedValues` owns only the *list structure* — comma splitting
(quote-aware) and multi-line combining — and keeps entries **verbatim**.
Interpreting an `X-Forwarded-For` entry as an address is left to the consumer via
`HttpForwardedNode.TryParse`, so the same list type serves all three
`X-Forwarded-*` headers even though their entries mean different things (address
vs. scheme vs. host).

### Deterministic rejection is a security property

Both the node and the element parser are **total**: any malformed input yields
`TryParse == false` with no exception, no unbounded recursion, and no super-linear
scanning (the quote-aware delimiter scan and the bracket/colon splits are all
single passes). The `Forwarded` list parser is **strict and all-or-nothing** — an
empty slot between commas is ignored per the RFC 7230 §7 list rule, but any
element that is present and malformed fails the *entire* header. This is
deliberate: a lenient parser that silently dropped one unparseable hop would let
the #778 trust evaluator mis-count the chain and attribute the request to the
wrong client. Strict parsing keeps the "how many hops, and which" question
answerable only from well-formed input. `for`/`by` values that are present but
are not valid nodes fail their element for the same reason. The fuzz suite
(`HttpForwardedFuzzTests`) pins totality and determinism across truncated quotes,
bracket/colon bombs, embedded NULs, high-plane characters, and multi-kilobyte
inputs.

### Rightmost-first traversal

Both list types hold entries in **wire order** (left-most = closest to the
client, right-most = closest to this server) and expose the same rightmost-first
affordances the trust evaluator needs: index + `Count`, a `Nearest` accessor for
the hop that handed *us* the request, and a `Reverse()` that returns a same-typed
list in nearest-hop-first order. `AsSpan()` gives allocation-free traversal.

### Why static-shaped value objects, not interfaces

Like `HttpMediaType` and the `StructuredField*` types, these are immutable
protocol primitives with exactly one correct parse — there is no injectable
behavior to abstract, so an `IForwarded…` interface would add allocation and
virtual dispatch (AOT-hostile) for no substitutability. The surface is kept
conservative on purpose: as a Stage-1 fan-out primitive (#778 and every
forwarding-aware concern imports it), it is costly to change once consumed.

### AOT posture

Span-based parsing over BCL `IPAddress`/char primitives — no reflection, no
runtime codegen, no dynamic serialization. Parameter/entry storage is a plain
array (no `ImmutableArray` dependency). Builds clean under the trim/AOT analyzers
(`IsAotCompatible=true`).

### Non-goals

- **The trust model.** `KnownProxies`/`KnownNetworks`/`ForwardLimit` and the
  walk that applies them belong to the #778 middleware in
  `Assimalign.Cohesion.Web.ForwardedHeaders`, not here.
- **The effective-identity feature contract.** `IHttpForwardedFeature` and the
  `Effective*` read convention are the layered `Assimalign.Cohesion.Http.Forwarded`
  package (see above) — the core stays protocol-only, and nothing ever mutates
  the wire-level request/connection state.
- **Interpreting `X-Forwarded-For` entries as addresses.** `HttpForwardedValues`
  is a faithful ordered list; turning an entry into an `IPAddress` (and deciding
  whether to trust it) is the consumer's call via `HttpForwardedNode`.
- **De-obfuscating identifiers.** RFC 7239 §6.3 obfuscated nodes/ports are
  recognized and preserved, never reversed — the mapping is private to the proxy
  that issued them.
- **`X-Forwarded-Port` and other vendor headers.** The scope is the three
  ubiquitous `X-Forwarded-*` headers plus RFC 7239; other vendor variants are a
  consumer overlay if ever needed.

## The QUERY method (RFC 10008)

RFC 10008 registers `QUERY`: a **safe, idempotent** method that carries the query
in the request **content** rather than the request target, ending the
POST-for-search workaround (a body that no longer defeats caching, and a target
URI no longer bounded by the query's size). The wire already accepts arbitrary
token methods and frames a body from the headers on all three transports, so the
method is a *semantics* addition to the core value-object layer — not a transport
change. This section records the three semantic decisions QUERY forced.

### Method classification lives on `HttpMethod`

`HttpMethod` gained four `bool` classification properties — `IsSafe`,
`IsIdempotent`, `IsCacheable`, and `CacheKeyIncludesContent` — because the
*method* is the correct home for facts the RFC defines *about the method*, and
every consumer that reasons about them (antiforgery, a future output cache,
retry policy, a CORS preflight decision) already holds an `HttpMethod`. They are
plain switches over the canonical (upper-cased) token — fully AOT/trim-safe, no
layering impact — and they answer:

| Method | `IsSafe` | `IsIdempotent` | `IsCacheable` | `CacheKeyIncludesContent` |
|---|---|---|---|---|
| GET, HEAD | ✓ | ✓ | ✓ | ✗ |
| OPTIONS, TRACE | ✓ | ✓ | ✗ | ✗ |
| **QUERY** | ✓ | ✓ | ✓ | **✓** |
| PUT, DELETE | ✗ | ✓ | ✗ | ✗ |
| POST | ✗ | ✗ | ✓ | ✗ |
| PATCH, CONNECT | ✗ | ✗ | ✗ | ✗ |
| *unknown extension token* | ✗ | ✗ | ✗ | ✗ |

`IsSafe`/`IsIdempotent` are RFC 9110 §9.2.1–9.2.2; `IsCacheable` is the RFC 9110
§9.2.3 "defined as cacheable" set (GET/HEAD/POST) plus QUERY. An **unknown**
extension token reports `false` for all four — its semantics are unknown, so the
conservative classification is "no property assumed". This makes the properties
total (a default-constructed `HttpMethod`, whose `Value` is `null`, also reports
`false` across the board via the switch's default arm).

`HttpMethod.Query` and the `"QUERY"` arm in `GetCanonicalizedValue` were the only
other additions. Adding the constant is **non-breaking**: `HttpMethod` equality
is `OrdinalIgnoreCase` over the token, so any pre-existing `new HttpMethod("QUERY")`
already compared equal to the new canonical value.

### `Accept-Query` is an SFV consumer, projected onto `HttpMediaType`

`HttpAcceptQuery` is the field-specific consumer for the `Accept-Query` response
header (RFC 10008 §3) — the header a resource emits to advertise QUERY support and
the query-format media type(s) it accepts. Per §3 the field is an RFC 9651
Structured Field **List** whose members are media ranges expressed as **either
Tokens or Strings** ("the choice … is semantically insignificant"), with
media-type parameters carried as structured-field parameters
(`"application/jsonpath", application/sql;charset="UTF-8"`).

Two reuse decisions keep it thin:

- **Parsing goes *through* `StructuredFieldList`**, not around it — the same
  discipline `HttpPriority` and the digest fields follow. `HttpAcceptQuery` only
  adds the media-range interpretation: it rejects an inner-list member, reads the
  bare Token/String, folds the member's parameters back into canonical textual
  form (`item.Parameters.Serialize()`), and hands the reconstructed
  `type/subtype;params` to `HttpMediaType.TryParse`. So an advertised range shares
  **one** representation with `Accept`/`Content-Type`, and `Accepts(contentType)`
  reuses `HttpMediaType.Includes` (RFC 9110 §12.5.1 matching) instead of a second
  media-range engine.
- **Serialization emits canonical SFV**: each range is a Token when it matches the
  `sf-token` grammar (media ranges almost always do — `/` and `*` are token
  characters) and a String otherwise, with parameters as structured-field String
  parameters. A parse→serialize round-trip therefore normalizes to canonical form
  (e.g. a quoted `"application/sql"` token-normalizes) rather than preserving the
  wire spelling — the same posture as the rest of the SFV toolkit.

Like the other core field types it is a `readonly struct` with
`TryParse`/`Parse`/`Serialize` and value equality, not an interface — an immutable
protocol value with one correct parse and no injectable behavior.

### Caching: the core supplies the method facts; the cache supplies the policy

RFC 10008 §2.7 makes QUERY responses cacheable **with the request content in the
cache key**. The core expresses exactly the two method-level facts a cache needs —
`IsCacheable` (the response *may* be stored) and `CacheKeyIncludesContent` (the
key MUST incorporate the request content and its related metadata) — and stops
there, mirroring the established split with the RFC 9111 caching primitives
(#755): `HttpCacheControl`/`HttpFreshness` supply the *directive parsing and
freshness arithmetic*, and the **policy** (a store, cache-key derivation,
revalidation, the POST/QUERY "explicit freshness required" rule) belongs to the
consuming cache — the future server-owned output cache (#795). A cache composes
the two: `method.IsCacheable && (has explicit freshness) → store`, keyed on
`method + target + (method.CacheKeyIncludesContent ? request content : ∅) + Vary`.
Keeping the fact in the core and the policy in the cache is what lets #795 be
built without re-deriving method semantics from the RFC.

### Antiforgery: QUERY is spec-safe but stays token-required

The one deliberately non-mechanical decision. `HttpAntiforgeryService.IsSafeMethod`
exempts only the **bodiless** safe methods (GET/HEAD/OPTIONS/TRACE) from token
validation. QUERY is spec-safe and idempotent — `HttpMethod.IsSafe` reports `true`
— but it **carries a request body**, so its CSRF exposure parallels POST: a
cross-site `<form>`/`fetch` can drive a state-influencing QUERY the same way it can
a POST. QUERY is therefore **not** exempt (a QUERY with no valid token is
rejected), and the antiforgery exempt set stays an explicit list rather than being
repointed at `HttpMethod.IsSafe` — repointing would silently exempt QUERY and
regress the protection. The rationale is recorded in code at `IsSafeMethod` and
pinned by tests; if a caller genuinely wants read-only QUERY endpoints exempt,
that is an explicit opt-in for that endpoint, not a change to the default.

### AOT posture

The classification properties are token switches; `HttpAcceptQuery` parses and
serializes via the span-based SFV toolkit and `HttpMediaType`. No reflection, no
runtime codegen, no dynamic dispatch. Builds clean under the trim/AOT analyzers
(`IsAotCompatible=true`).

### Non-goals

- **The server MUST-level request rules.** RFC 10008 §2.1/§2.3 (reject a QUERY with
  a missing/inconsistent `Content-Type`), §2.5 (do not rewrite a QUERY redirect to
  GET), and §2.6 (conditional QUERY behaves like conditional GET) are *server
  request-handling* behavior, not value-object semantics. They belong to the
  request pipeline / transport that consumes these primitives, which reuses the
  existing conditional-request (`HttpConditionalRequest`) and redirect machinery;
  the core only supplies the method classification and `Accept-Query` model.
- **A cache.** As above — no store, cache-key derivation, or revalidation here.
- **Field wiring.** Reading `Accept-Query` off a response, emitting it, and the
  content-type negotiation for QUERY content are the consumer's job, consistent
  with the media-type and caching sections.
