# Assimalign.Cohesion.Web.Serialization — Design

The content-serialization half of the #864 pipeline-formatting design (the other half is the
`Web.ErrorHandling` `OnError` hook). This package exists because the Web area is
**middleware-first**: handlers are middleware that write responses imperatively (the IResult
return-value model was implemented and deliberately withdrawn before merge on PR #887 — with no
return-value handler seam in the pipeline, a result abstraction had nothing to attach to). What
handlers actually needed from that design was typed body IO without serializer ceremony; this
registry is that seam, kept deliberately small so content negotiation (#149) and
source-generated binding (#796) can build on it without re-litigating formatting.

## Design intent

One registry per application, composed at builder time and seeded onto every exchange as a
typed feature, answering exactly two questions: *which component deserializes a request body of
this media type into this CLR type* and *which component serializes this CLR type as this media
type onto the response*. Everything else — choosing the media type from `Accept`, validating
the deserialized model, mapping outcomes to status codes — is deliberately someone else's
layer, composing over the same seam.

## Why-this-not-that decisions

- **Two halves, not one `ISerializer`.** Request deserialization and response serialization are
  registered and resolved independently (`IHttpContentReader` / `IHttpContentWriter`). A single
  god interface forces every format to implement both directions and couples negotiation to
  deserialization; the halves keep asymmetric formats (a read-only form parser, a write-only
  event stream) honest.
- **Non-generic contracts, generic extensions.** The interfaces take `(object? value, Type type)`
  rather than `<T>`: generic virtual dispatch is a NativeAOT sizing/perf hazard, and a
  heterogeneous registry needs uniform storage anyway. Typed ergonomics
  (`ReadContentAsync<T>`, `WriteContentAsync<T>`) are non-virtual extensions that forward
  `typeof(T)` — fully shareable code under AOT. The boxing cost on struct writes is accepted;
  #796's generated call sites can revisit if it ever shows up.
- **Matching is #771 semantics, nothing invented.** Registrations declare `HttpMediaType`
  ranges; lookup is `range.Includes(candidate)` with `Specificity` ranking and
  registration-order tie-break — the same RFC 9110 §12.5.1 rules `Accept` handling uses, so
  #149's negotiation and this registry can never disagree about what matches. Consequence: the
  `application/*+json` structured-suffix convention is **not** a match rule here (it is not an
  RFC media range); the JSON pair claims `application/json` + `text/json`. Suffix awareness is the
  negotiation layer's call, via `HttpMediaType.Suffix` — delivered narrowly (see *Content
  negotiation* below).
- **`AddJsonSerialization(resolver)` is the AOT registration story.** The built-in JSON pair
  serializes exclusively through the `JsonTypeInfo`-based System.Text.Json entry points, with
  contracts supplied by the application's source-generated resolver (typically its
  `JsonSerializerContext`). There is no reflection fallback: options are frozen with
  `MakeReadOnly()` (the non-populating overload), and a type outside the resolver's contracts
  faults with `HttpContentSerializationException` instead of silently reflecting. Options
  default to `JsonSerializerDefaults.Web` (camelCase, case-insensitive reads).
- **Builder-time feature, no DI** — the `AddAuthentication` idiom. The root verb creates the
  registry and attaches it via `IWebApplicationBuilder.AddFeature`; the returned
  `ContentSerializationBuilder` feeds the same instance, so chained format verbs (`AddJson`,
  future grafts) take effect without re-registration. Repeating the *root* verb composes a
  fresh registry that replaces the old one (features are name-keyed) — call it once.
- **`ReadContentAsync`/`WriteContentAsync`, not `WriteAsync`.** The issue sketch says
  `response.WriteAsync(value)`; the shipped names add `Content` because a raw-text
  `WriteAsync(string)` response helper is a likely future addition, and an unconstrained
  `WriteAsync<T>` would silently change which overload a `string` argument binds to across
  recompiles. `Content` also names the seam (HTTP content, the registry family prefix).
- **Throwing call sites over a non-throwing feature.** `GetReader`/`GetWriter` return
  `null` — that is the branch outcome-producing layers use to emit `415`/`406` responses. The
  extensions throw `HttpContentSerializationException` because at a bare call site an
  unresolvable read/write is a composition gap (a fault for the `OnError` hook), not a protocol
  outcome. Both postures are the same faults-vs-outcomes line the error-handling package
  documents.
- **Default write target = first registered writer.** `WriteContentAsync(value)` with no
  explicit media type uses the response's already-set `Content-Type` when present, else the
  first registered writer's canonical type (its first declared media type, validated concrete
  at registration). Deterministic and documented; `Accept`-driven selection is the negotiation
  layer (#149), which passes its choice through the explicit-media-type overload — see *Content
  negotiation* below.
- **The declared type is the contract.** `WriteContentAsync<T>` serializes as `typeof(T)`, not
  `value.GetType()` — polymorphic-by-runtime-type serialization is an implicit-reflection shape
  that undermines the explicit-contract story; STJ's declared-type polymorphism options remain
  available through the resolver.

## Content negotiation (#149)

Server-driven negotiation composes *over* the registry as a thin layer, never redoing its work:
the q-value / precedence engine is the shared `HttpContentNegotiation` primitive (#771), and the
selectable representations are exactly the registry's writers. `HttpContentNegotiationExtensions`
ships three members — `IHttpContentSerializationFeature.TryNegotiate(acceptHeader, out mediaType)`
(the pure seam), `IHttpContext.TryNegotiateContentType(out mediaType)` (the same, reading the
exchange's `Accept`), and `IHttpContext.WriteNegotiatedContentAsync<T>(value, ct)` (negotiate →
write, or compose the `406`).

- **A seam, not a feature.** Negotiation is a stateless function of *(registered writers, `Accept`
  header)*, so it is an extension surface over the existing `IHttpContentSerializationFeature`,
  not a second feature seeded onto the exchange. A separate `IHttpContentNegotiator` feature
  would need its own builder wiring and would only re-read the writers the registry already holds;
  the extension composes with zero new composition surface and no DI, usable from any middleware.
  This matches the package's other stateless call-site extensions (`ReadContentAsync`,
  `WriteContentAsync`) rather than the interface-first rule that governs stateful contracts.
- **Server options are the writers' concrete media types.** Negotiation offers every concrete
  (wildcard-free) media type across the writers, in registration order, de-duplicated. A writer's
  wildcard entries are match *targets*, not representations it can emit, so they are excluded; a
  writer's alternate concrete types (the JSON pair's `text/json`) are offered. Registration order
  is server-preference order, so equal-quality ties and a wildcard `*/*` request resolve to the
  earliest writer — the same precedence the registry's `GetWriter` applies.
- **Structured-suffix fallback — the decision #864 deferred here.** The registry's `Includes`
  matching has no `+suffix` semantics (a recorded #864 non-goal). #149 owns the call and makes it
  narrowly: exact RFC 9110 §12.5.1 matching runs first; only when it finds nothing does a fallback
  let a **bare base-type** `Accept` range (`application/json`) be satisfied by a registered writer
  whose media type carries that base as its `HttpMediaType.Suffix` (`application/problem+json`).
  The response then carries the writer's honest concrete type, not the requested base.
  - *Direction.* A `+json` type is guaranteed parseable as JSON (RFC 6839), so a JSON-accepting
    client can consume it; the reverse does not hold, so an already-suffixed range
    (`application/vnd.foo+json`) is **not** widened — it names a specific schema.
  - *Precedence.* The fallback is strictly below exact matching, so it never changes an exact
    outcome; it only turns a would-be `406` into a served, correctly-typed response. The
    motivating case is an error surface that registers only `application/problem+json` still
    answering an `Accept: application/json` request.
  - *Refusals honored.* A `q=0` range covering the suffixed type (`application/problem+json;q=0`)
    still rejects it — the fallback re-checks explicit refusals before selecting.
  - *Not general suffix matching.* A client **range** like `application/*+json` is not honored:
    the #771 parser treats `*+json` as a literal subtype, not a suffix wildcard, and reproducing
    that here would mean re-implementing media-range parsing. Recorded as a primitive gap, not
    duplicated (see Non-goals).
- **`Vary: Accept`, appended.** A negotiated response depends on `Accept`, so
  `WriteNegotiatedContentAsync` stamps `Vary: Accept` on both the written response and the `406`.
  It appends — an existing `Vary` token (e.g. a CORS layer's `Origin`) is preserved, `Accept` is
  never duplicated, and a `Vary: *` is left untouched.
- **`406` is an outcome, not a fault.** A missing registry is still a composition fault (the
  call-site resolver throws, per the error model). But *no acceptable representation* — including
  an empty registry — is a protocol outcome: `WriteNegotiatedContentAsync` sets a **bodyless**
  `406` (no `Content-Type`, no body) and returns `false`, the exact shape the #881 status-code-pages
  middleware upgrades into a problem+json explanation. This is deliberately looser than the default
  `WriteContentAsync(value)`, which faults on an empty registry — a negotiated write's contract is
  "serve the best the registry can, else `406`", so it treats "nothing to offer" as the
  client-facing outcome. The pure `TryNegotiate` seam is non-throwing throughout (an unacceptable
  request is `false`, never an exception).

## Error model

`HttpContentSerializationException` (sealed, this package's root) covers exactly the
composition faults: no registry composed, no reader/writer for the media type, no contract for
the CLR type. Malformed payloads are **not** wrapped — format-native exceptions
(`JsonException`) propagate so boundaries and handlers can branch on them. Argument misuse
(wildcard media type to a write) is `ArgumentException`, not a serialization fault.

## AOT posture

No reflection-based serialization anywhere: contract lookup is `TryGetTypeInfo` against the
registered resolver, serialization goes through `JsonTypeInfo` overloads only, and the one
`typeof(T)` per typed call site is AOT-trivial. The package adds no source generators of its
own — it *consumes* the application's.

## Validation (recorded, deliberately not here)

Validation sits between deserialization and the handler. The registry's job ends when a typed
value exists; a validation layer (#796's source-generated binding is the planned consumer)
reads the same value and produces `400` outcomes. Folding validation into readers would force
every format to re-implement it and would blur the faults-vs-outcomes line (invalid input is an
outcome, not a fault).

## Homing under the hosting-isolation rule

A feature package on the area root's seams, per `.claude/rules/resource-areas.md`: it
references `Assimalign.Cohesion.Http` + `Assimalign.Cohesion.Web` only, ships its builder verbs
itself, and is delivered to applications through the `App.Web` shared framework.
`Web.Hosting` never references it (COHRES002) — the runtime seeds whatever features the
builder registered, with no compile-time knowledge of this package.

## Non-goals

- **Binding and validation** — #796, over this registry.
- **Structured-suffix wildcard ranges (`application/*+json`)** — the negotiation layer added a
  narrow base-type→suffix fallback (see *Content negotiation*), but a client range like
  `application/*+json` is still not honored; the #771 parser treats `*+json` as a literal subtype,
  and reproducing suffix-wildcard matching would mean re-implementing media-range parsing. A
  primitive-side gap, revisited if a real consumer needs it.
- **`Accept-Charset` / `Accept-Language` negotiation** — media types only; charset and language
  selection are a separate concern (the #771 primitive already parses them).
- **Client-side content negotiation** — this surface is server-side response media-type selection;
  the client half (setting `Accept`, reading a response's `Content-Type`) lives elsewhere.
- **Non-UTF-8 request transcoding** — the JSON reader takes the body stream as-is (UTF-8);
  charset transcoding is a documented gap until a real consumer appears.
- **Raw/string body helpers and SSE** — raw writes stay on `Body`;
  `Http.Streaming`/`Http.ServerSentEvents` own streaming shapes.
