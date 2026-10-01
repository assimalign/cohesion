# Assimalign.Cohesion.Web.Antiforgery — Overview

Cross-site request forgery (CSRF) protection for the Cohesion Web pipeline. The package wires the
`Assimalign.Cohesion.Http.Antiforgery` token engine (a signed double-submit cookie and request token)
into a Web application: it registers the application's antiforgery service, validates protected
endpoints in a middleware, and declares protection per endpoint with routing metadata. It never
reimplements token cryptography; it chooses the protector the engine seals tokens with.

## Scope

- **`AddAntiforgery`** (builder time) creates the application's `IHttpAntiforgery` service and
  registers it as an application feature, so every exchange carries it and a handler mints tokens with
  `context.RequireAntiforgery.GetAndStoreTokens(context)`.
- **Protector selection.** `AddAntiforgery(dataProtectionProvider)` seals tokens with a protector the
  application's `Security.DataProtection` key ring derives for the antiforgery purpose, so tokens
  survive restarts and validate on every instance that shares the key repository. `AddAntiforgery()`
  without a provider falls back to the engine's per-process random key, which is for **development
  only**: a restart invalidates every token, and instances reject each other's tokens.
- **`UseAntiforgery`** (pipeline time, after `UseRouting`) validates unsafe-method requests to endpoints
  that carry `AntiforgeryMetadata.Required`. Safe methods (`GET`, `HEAD`, `OPTIONS`, `TRACE`) and CORS
  preflights pass through; `QUERY` carries a body and is validated. A failed validation is answered with
  `400 Bad Request` as RFC 9457 `application/problem+json`, and the endpoint does not run.
- **Endpoint metadata and verbs.** `RequireAntiforgery()` and `DisableAntiforgery()` attach the sealed
  `AntiforgeryMetadata` to a route or a route group, most specific declaration winning. Every typed
  endpoint with a `[FromForm]` parameter requires antiforgery automatically: the Web endpoint-binding
  generator attaches the requirement when the application references this package.
- **Fail closed.** An endpoint that requires validation fails at dispatch with
  `InvalidOperationException` when `UseAntiforgery` is missing or registered ahead of `UseRouting`,
  instead of running unprotected.

## Dependencies

- `Assimalign.Cohesion.Web` — the builder, pipeline and middleware abstractions the verbs build on.
- `Assimalign.Cohesion.Web.Routing` — the published route match and metadata the middleware reads, the
  convention-builder seam the verbs extend, and the acknowledgement routing checks before dispatch.
- `Assimalign.Cohesion.Web.ProblemDetails` — the `application/problem+json` rejection.
- `Assimalign.Cohesion.Http.Antiforgery` — the token engine, `IHttpAntiforgery`, and its options.
- `Assimalign.Cohesion.Http.Forms` — the form read for the form-token flow.
- `Assimalign.Cohesion.Http.Streaming` — the committed-response check before a rejection.
- `Assimalign.Cohesion.Security.DataProtection` — the purpose-bound protector behind production tokens.
- `Assimalign.Cohesion.Http` — the HTTP context, methods, media types, and header keys.

It never references `Assimalign.Cohesion.Web.Hosting` or any `Assimalign.Cohesion.Hosting*` library
(the resource hosting-isolation rule, `COHRES001`/`COHRES004`). The package and `Http.Antiforgery` are
members of the `App.Web` shared framework.

## Usage

```csharp
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Security.DataProtection;
using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.Antiforgery;
using Assimalign.Cohesion.Web.Hosting;
using Assimalign.Cohesion.Web.Routing;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// One key ring for every protected artifact; point every instance at the same repository.
IDataProtectionProvider dataProtection = DataProtectionProvider.Create(
    KeyRepository.CreateFileSystem("/var/lib/app/keys"));

builder.AddRouting();
builder.AddAntiforgery(dataProtection);  // the cookie token is Secure on every HTTPS request
// builder.AddAntiforgery();             // development only: a per-process random key

WebApplication app = builder.Build();

app.UseRouting();
app.UseAntiforgery();          // after UseRouting

// The render path mints the pair: the cookie token is set on the response, and the request token
// goes into a hidden field (tokens.FormFieldName) or to a script that sends it in tokens.HeaderName.
app.MapGet("/contact", async context =>
{
    HttpAntiforgeryTokenSet tokens = context.RequireAntiforgery.GetAndStoreTokens(context);
    await RenderContactFormAsync(context, tokens);
});

// A typed form-bound endpoint requires antiforgery without a convention call.
app.MapPost("/contact", async ([FromForm] string email, IHttpContext context) => { /* ... */ });

// Raw endpoints and groups declare it; a route opts out of its group's requirement.
app.Map(HttpMethod.Post, "/settings", SaveSettingsAsync).RequireAntiforgery();
IRouterGroupBuilder account = app.MapGroup("/account").RequireAntiforgery();
account.Map(HttpMethod.Post, "webhook", ReceiveWebhookAsync).DisableAntiforgery();
```

To share the key ring cookie authentication uses, pass the same provider to both
(`builder.AddAuthentication(dataProtectionProvider: dataProtection)` and
`builder.AddAntiforgery(dataProtection)`); each derives its own purpose, so neither accepts the other's
payloads.

Register `UseAntiforgery` after `UseRouting`, and after `UseRateLimiting` and `UseRequestTimeouts` when
the application uses them, so floods are rejected before any body is read and a form read runs under the
endpoint's timeout. A client that sends the token in the header keeps its body unread by the middleware,
which matters for endpoints that stream large uploads.

See `docs/DESIGN.md` for the token flow, the protector selection, the generator integration, the
fail-closed rule, and the non-goals.
