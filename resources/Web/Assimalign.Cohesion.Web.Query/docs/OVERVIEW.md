# Assimalign.Cohesion.Web.Query — Overview

Server-side handling for the RFC 10008 HTTP **QUERY** method in the Cohesion Web pipeline. The
core value-object layer (`HttpMethod.Query`, its safe/idempotent/cacheable classification, and
the `Accept-Query` field model `HttpAcceptQuery`) ships in `Assimalign.Cohesion.Http`; this
package adds the *application decisions* the RFC's MUST-level server rules call for —
middleware-first, every response written imperatively.

## Scope

- **Request-content validation (RFC 10008 § 2.1 / § 2.3)** —
  `builder.Services.AddQueryValidation(validation => ...)` and `UseQueryValidation()`:
  query content must declare a parseable `Content-Type` (else `400`, or `415` per policy); the
  declared type must fall within the resource's advertised `Accept-Query` set (else `415`, with
  the accepted set advertised on the rejection); the request's `Accept` field must be
  satisfiable against the resource's producible representations (else `406`). Configured
  through `WebQueryValidationBuilder.Options` (a `WebQueryValidationOptions`).
- **Method-preserving redirects (RFC 10008 § 2.5)** — `response.RedirectQuery(location,
  permanent)` emits `307`/`308` (never `301`/`302`, whose legacy GET rewrite would drop the
  query content), and `response.RedirectQueryToGet(location)` emits the one sanctioned method
  switch, `303 See Other`. The client half — a redirect-following client that re-issues QUERY
  with its content — lives in `Assimalign.Cohesion.Http.ClientFactory`.
- **Conditional QUERY (RFC 10008 § 2.6)** —
  `builder.Services.AddQueryConditionals(conditionals => conditionals.UseResourceValidators(provider))`
  with `UseQueryConditionals()`, and the
  `EvaluateQueryPreconditions` / `TryHandleQueryPreconditions` context helpers, evaluate the
  `If-*` precondition fields exactly as for the equivalent conditional GET — reusing the core
  `HttpConditionalRequest` evaluator — and answer `304` / `412` without executing the query.

## Dependencies

`Assimalign.Cohesion.Web` (the pipeline seams) and, through it, `Assimalign.Cohesion.Http`
(the QUERY/media-type/conditional primitives). No DI, no configuration binding, no reference to
`Web.Hosting` — the package follows the Web-area dependency rule. The `builder.Services.Add*`
verbs are component integrations the source generator projects into the application, so the
package itself takes no container reference.

## Usage

```csharp
builder.Services.AddQueryValidation(validation =>
{
    validation.Options.AcceptedMediaTypes.Add(HttpMediaType.Parse("application/json"));
    validation.Options.SupportedResponseMediaTypes.Add(HttpMediaType.Parse("application/json"));
});

builder.Services.AddQueryConditionals(conditionals => conditionals.UseResourceValidators(async context =>
    await catalog.TryGetValidatorsAsync(context.Request.Path) is { } current
        ? new WebQueryResourceValidators { ETag = current.ETag, LastModified = current.LastModified }
        : null));

WebApplication app = builder.Build();

app.UseQueryValidation();
app.UseQueryConditionals();

app.Use(async (context, next) =>
{
    // ... execute the query; or hand the client a stored result to GET:
    context.Response.RedirectQueryToGet($"/results/{resultId}");
});
```

Configuration lives on the `Add` verbs only ("Add configures, Use places"). Each `Use` verb takes
no arguments and places the middleware its `Add` verb registered (the last registration when there
are several). `UseQueryValidation()` places one with the defaults when the application registered
none: any valid `Content-Type`, no response negotiation, `400` for untyped content.
`UseQueryConditionals()` has no default, because only the application can resolve its resources'
validators: without a registration, composing the pipeline fails with an
`InvalidOperationException` that names `AddQueryConditionals`, and a registration that never calls
`UseResourceValidators` fails where it is registered.

Design rationale (why middleware and not a transport interceptor, snapshot semantics, status
choices, non-goals) lives in [DESIGN.md](DESIGN.md).

Redirect locations use `HttpFieldValue`; string calls use its implicit conversion.
Pass a non-empty absolute or relative reference with no control character or
space (`%20` represents a space). Invalid or default locations throw
`ArgumentException` before changing the response. The helper parameters changed
from `string` in 10.0.0-preview.1, so rebuild consumers.
