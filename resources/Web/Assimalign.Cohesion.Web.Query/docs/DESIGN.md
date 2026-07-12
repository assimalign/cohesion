# Assimalign.Cohesion.Web.Query — Design

## Design intent

RFC 10008 splits cleanly along the repo's delineation rule (`libraries/Http/…/docs/DESIGN.md`,
*"mechanisms live at the lowest level that can observe what they need; decisions live at the
application level"*). The mechanisms — the method token and its classification, the
`Accept-Query` field model, media-range matching, the § 13.2.2 precondition evaluator — are core
`Assimalign.Cohesion.Http` value objects (#746, #771, #755/#792). What remained of the RFC's
MUST-level server rules are *decisions*: which status rejects an untyped query, what a resource
accepts and produces, how a redirect is shaped, whether a precondition resolves the exchange.
This package is where those decisions live, as Web middleware and imperative response helpers.

## The interceptor-vs-middleware decision (§ 2.1 / § 2.3 enforcement)

The umbrella work item left open whether the *generic* refusal — "query content MUST declare a
Content-Type" — should ride a parse-time `IHttpExchangeInterceptor` (throwing the typed
`HttpRequestRejectedException`) with only the per-resource negotiation as middleware. **Decided:
both halves are one Web middleware (`UseQueryValidation`); no transport interceptor.** Why:

- **A rejection is not proportionate here.** `HttpRequestRejectedException` answers with a
  minimal response and then **closes the connection** — by contract, because a parse-path
  rejection leaves the wire state indeterminate. A QUERY with a missing or malformed
  `Content-Type` is *well-framed*: the transport knows exactly where the message ends, nothing
  about the exchange is hazardous, and killing an HTTP/1.1 keep-alive (or forcing h2 stream
  churn) for a routine `4xx` punishes conforming clients sharing the connection.
- **The two halves share one policy.** "Missing type → 400 or 415" is configurable per
  resource, and "type outside the accepted set → 415" *is* the per-resource half. Splitting
  them across a listener-wide transport seam and a per-app middleware would put one 400/415
  line in two places with two scopes.
- **The delineation rule points the same way.** Refusing to process a parseable, well-framed
  request is a policy about the *application's* resources, not a wire-conformance mechanism.
  Interceptors are for what must be observed at parse time (limits, digests, framing); nothing
  here needs parse-time observation — the headers are all still there at dispatch.

The middleware short-circuits **without reading the request body**; realigning or retiring the
connection around an unread body is the transport's existing responsibility (it drains or
closes as needed — covered by an end-to-end test that reuses the client after a rejection).

## Validation semantics (the exact checks, in order)

For QUERY requests only (everything else passes through untouched):

1. **Advertise** — when `AcceptedMediaTypes` is configured (and `AdvertiseAcceptQuery` is on),
   stamp the serialized `Accept-Query` field on the response up front (RFC 10008 § 3), so
   every response from the resource — rejections included — signals QUERY support. Stamped
   before `next`, so an application handler can still override it.
2. **Declared `Content-Type` is validated whenever present** — malformed → `400`/`415` per
   `InvalidContentTypeStatusCode`; parseable but outside the `Accept-Query` set (RFC 9110
   § 12.5.1 `Includes` matching via `HttpAcceptQuery.Accepts`) → `415`. Validating the header
   independently of body detection is what keeps HTTP/2+ honest (see below).
3. **Missing `Content-Type` with detected content** → `400`/`415` per policy. Content is
   detected from message metadata only: `Content-Length` when declared, then
   `Transfer-Encoding`, then a buffered body's observable length. **Accepted limitation:** an
   HTTP/2+ request that *streams* content with no `Content-Length` is not detectable at head
   time without consuming the body — such a request passes through, and a conformant client's
   `Content-Type` was already validated by step 2. On HTTP/1.1 detection is exact (RFC 9112
   § 6: no framing headers, no body).
4. **Response negotiation** — when `SupportedResponseMediaTypes` is configured, the request's
   `Accept` is negotiated via the core `HttpContentNegotiation`; no acceptable representation →
   `406`. A missing `Accept` accepts everything (RFC 9110 § 12.5.1).

A **bodiless QUERY passes through**: § 2.3's MUST attaches to request content, and whether an
empty query is meaningful is the application's call.

**`422 Unprocessable Content` is deliberately not emitted here.** A parseable, accepted
`Content-Type` whose *content* turns out semantically invalid (a malformed JSONPath, an
unbindable filter) is an application judgment made while interpreting the query — middleware
cannot know it generically. Applications answer `422` themselves at that point.

## Options snapshot at `Use` time

`UseQueryValidation` snapshots `WebQueryValidationOptions` into the middleware when it runs —
the accepted set becomes an immutable `HttpAcceptQuery` (serialized once), the response set an
array. Mutating the options object afterwards has no effect, matching the Web pipeline's
builder-time composition model (the same freeze posture as route groups). The
`InvalidContentTypeStatusCode` setter validates `400`/`415` at assignment, so a bad policy fails
at composition, not per request.

## Redirect helpers never emit 301/302 for a query (§ 2.5)

`RedirectQuery` chooses `307 Temporary Redirect` / `308 Permanent Redirect`; `RedirectQueryToGet`
is the explicit `303 See Other` hand-off (§ 2.5.3, e.g. to a stored result resource). `301`/`302`
are deliberately unavailable through this surface: user agents historically rewrite them to GET
(the legacy POST behavior RFC 9110 § 15.4.2/§ 15.4.3 records), which silently drops the query
content — the "implied GET downgrade" the RFC warns about. The helpers are method-agnostic
mechanically (status + `Location` only, no body) but exist so redirecting a QUERY is
correct-by-construction. The client half of § 2.5 — a redirect-following client that re-issues
QUERY with its original content and switches methods only on `303` — is
`Assimalign.Cohesion.Http.ClientFactory`'s factory-owned redirect layer (see its `DESIGN.md`).

## Conditional QUERY reuses the core evaluator (§ 2.6)

§ 2.6's rule is *"evaluate as the equivalent conditional GET"* — so the method classification
lives in the core evaluator itself: `HttpConditionalRequest` treats QUERY as a read method
(GET/HEAD/QUERY), the same layer that owns the § 13.2.2 ordering. This package adds only what
the core deliberately leaves to consumers (its "reading the fields is the consumer's job"
posture): parsing `If-Match`/`If-None-Match`/`If-Modified-Since`/`If-Unmodified-Since` off the
request (malformed fields are ignored, the § 13.1.3 posture), building the evaluation context,
and shaping the outcome — `304` carrying the validators the `200` would have (RFC 9110
§ 15.4.5), `412` bare, both bodiless.

Two surfaces, one evaluation path:

- **`EvaluateQueryPreconditions` / `TryHandleQueryPreconditions`** (context helpers) — for
  handlers, which typically learn the resource's validators mid-flight. Call before executing
  the query; a `true` from the try-form means `304`/`412` was written and the query must not
  run (RFC 9110 § 13.1.2).
- **`UseQueryConditionals(provider)`** (middleware) — for resources whose validators are cheap
  to resolve up front. The `WebQueryResourceValidatorsProvider` returns `null` when validators
  are unknown (pass through, no evaluation). On `Proceed` the middleware stamps
  `ETag`/`Last-Modified` onto the response — app-overridable — so clients can condition their
  next query; this is also the seam the future server output cache composes with.

The middleware touches QUERY requests only. Conditional GET/HEAD shaping belongs to the features
that own those flows (static files, output caching), not to this package.

## Error model

No new exception types. Rejections are imperative status writes; option misuse throws BCL
argument exceptions at composition time.

## AOT posture

Value objects, delegates, and span-based parsing from the core — no reflection, no dynamic
code, no serialization.

## Non-goals

- **No `422` emission** (application judgment — above).
- **No request-body inspection** of any kind: validation is header/metadata-only by design.
- **No general-purpose redirect or conditional-GET surface** — this package is scoped to the
  QUERY method; a general conditional feature would lift the same core primitives.
- **No `OPTIONS` handling or standalone `Accept-Query` advertisement endpoint** — the
  advertisement rides the responses of the resource itself.
- **No DI, no configuration binding, no request-time service location** — options and
  delegates only, per the Web-area feature-package rules.
