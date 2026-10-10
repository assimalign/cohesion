# Assimalign.Cohesion.Web.Forms — Design

## Design intent

A single-purpose bridge that plugs HTTP form parsing
(`Assimalign.Cohesion.Http.Forms`) into the Web application middleware
pipeline (`Assimalign.Cohesion.Web`). It owns exactly one thing: the
`UseForms()` pipeline-builder extension. All of the parsing intelligence —
the streaming urlencoded/multipart readers, the limits, the spill-to-disk
buffering, the `IHttpFormFeature` seam — lives in `Http.Forms`. This project
is the resource-layer wiring, kept separate so that:

- the protocol-layer form model stays reusable outside the Web host, and
- a Web app opts into form parsing by adding this one package, without the
  Web runtime taking a hard dependency on a form model.

## What `UseForms()` does

`UseForms()` registers the internal `FormsMiddleware`, which, per request:

1. Looks for an existing `IHttpFormFeature` in `context.Features`.
2. Installs a default `HttpFormFeature` over `context.Request` when none is
   present (so a middleware earlier in the pipeline can pre-install a custom
   feature and win).
3. Calls `ReadFormAsync(context.RequestCancelled)` to parse the body eagerly.
4. Answers a form the parse rejects and stops there (next section), or
   otherwise invokes the next middleware.

After it runs, downstream middleware reads `context.Request.Form`
synchronously — the parse has already happened and the result is cached on the
feature.

## An unreadable form is the client's error (#1210)

The parse runs ahead of every route, so a form it rejects must not become a
fault. Before #1210 the `InvalidDataException` escaped the middleware: any
client could post an oversized or malformed form to any route and get a `500`,
with an Error-level fault log at the exception boundary. The middleware now
answers it and the rest of the pipeline does not run:

| The parse throws | Answer |
| --- | --- |
| `InvalidDataException` whose `InnerException` is `HttpFormLimitExceededException` (a body over a configured `HttpFormOptions` limit) | `413 Content Too Large` (RFC 9110 §15.5.14), `application/problem+json`, detail "The request form exceeds a configured size limit." |
| Any other `InvalidDataException` (a malformed body) | `400 Bad Request`, `application/problem+json`, detail "One or more binding errors occurred." and `errors` keyed `$form` |
| Anything else | Not caught (below) |

- **The same payload as a form-bound endpoint.** Both answers are byte for byte
  what the endpoint-binding generator's form read writes for the same failure,
  so a client gets one answer whether `UseForms()` or the endpoint parsed the
  form. The limit is told apart from a malformed body by the cause's type, not
  by the message (Http.Forms DESIGN).
- **The limits are the feature's.** The defaults apply to the feature this
  middleware installs. An application that installs
  `new HttpFormFeature(context.Request, options)` ahead of `UseForms()` gets
  its own limits answered `413` the same way.
- **A committed head aborts instead.** The parse runs before `next`, so the head
  is normally still writable. When a middleware ahead of this one has already
  committed it (`IHttpResponseStreamingFeature.HasStarted`), the status can no
  longer be set and a problem body would land inside another response, so the
  middleware cancels the exchange instead, as `UseAntiforgery` does.
- **What is not caught.** A body over the transport's own cap, an over-limit
  decompressed body (`Web.Compression`), an `IOException` from a broken
  connection and a cancelled request belong to their owners, exactly as they do
  for a form-bound endpoint.

The failed parse stays cached on the feature, so nothing downstream could read
the form anyway: answering here is the only place the request can still get a
client-error status.

## Eager vs. lazy — a deliberate, revisitable choice

`UseForms()` parses **every** request that flows through it, regardless of
Content-Type. Non-form bodies (`application/json`, no body, etc.) yield an
empty collection cheaply because the feature short-circuits on an unrecognized
media type without draining the stream. The trade-off is that a request whose
form is never read still pays a small detection cost.

This is intentional for the common "forms app" shape where handlers expect
`request.Form` to be populated. Apps that only need forms on specific routes
should skip `UseForms()` and call `context.ReadFormAsync(...)` lazily inside
those handlers instead. A future `UseForms(options)` overload could add a
content-type predicate or a lazy mode; that is a additive API change, not a
redesign, and is deliberately not built until a consumer needs it.

## Boundaries

- **No parsing logic here.** If a form-parsing behavior needs changing, it
  changes in `Http.Forms`, not in this middleware.
- **No DI/logging/config coupling.** The middleware reads and mutates the
  `IHttpContext` feature collection, and writes the response only to reject an
  unreadable form; it does not resolve services at request time. Extensibility
  is via installing a different `IHttpFormFeature`, not service location.
- **References.** The Web root and `Http.Forms`, plus `Web.ProblemDetails` for
  the rejection payload (feature to feature, which the Web dependency rule
  allows) and `Http.Streaming` for the committed-head check. All four are
  already App.Web members, and none is a `Hosting*` library.

## Non-goals

- A model binder (form → typed object) — that belongs to the Web API /
  source-generation layer.
- Per-route form configuration — deferred until a real need appears (see the
  eager/lazy note above).
- Antiforgery validation — that is `Assimalign.Cohesion.Web.Antiforgery`
  (#1057). It also protects header-token requests that carry no form, so it is
  not a form concern. For the form-token flow it reads the form through the same
  `IHttpFormFeature` parse cache, so `UseForms()` ahead of it costs no second
  parse.
