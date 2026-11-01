# Assimalign.Cohesion.Web.SecurityHeaders — Overview

Browser security headers for the Cohesion Web pipeline. `builder.Services.AddSecurityHeaders(...)`
configures the policy and `UseSecurityHeaders()` places the middleware, which puts safe defaults on every
response that passes through it and lets an application opt into a Content Security Policy (with
per-request nonces), a Permissions Policy, and the cross-origin isolation fields. Endpoints can adjust,
replace or disable the policy through routing metadata.

## What it provides

- `AddSecurityHeaders(headers => ...)` and `UseSecurityHeaders()`: the registration verb on
  `builder.Services`, a generated component integration over `SecurityHeadersBuilder`, configures the
  policy (`SecurityHeadersBuilder.Options`, a `SecurityHeadersPolicy`), and the pipeline verb on
  `IWebApplicationPipelineBuilder` places the middleware. With no configuration it emits:
  - `X-Content-Type-Options: nosniff`;
  - clickjacking protection: `Content-Security-Policy: frame-ancestors 'none'` and
    `X-Frame-Options: DENY`;
  - `Referrer-Policy: strict-origin-when-cross-origin`.
- Opt-in fields on `SecurityHeadersPolicy`: `ContentSecurityPolicy` and
  `ContentSecurityPolicyReportOnly` (built with `ContentSecurityPolicy.Create`), `PermissionsPolicy`
  (built with `PermissionsPolicy.Create`), `CrossOriginOpenerPolicy`, `CrossOriginEmbedderPolicy` and
  `CrossOriginResourcePolicy`. `Framing` (a `FramingPolicy`) changes or removes the clickjacking
  protection; `ReferrerPolicy` changes or removes the referrer policy.
- Validating builders. `ContentSecurityPolicyBuilder` and `PermissionsPolicyBuilder` check every
  directive, source, feature name and origin against its specification's grammar when the policy is
  built, so a policy that would not parse the way it reads fails at startup instead of being partly
  ignored by the browser.
- A per-request nonce. `ContentSecurityPolicySourceListBuilder.Nonce()` puts a `'nonce-…'` source in a
  directive; handlers read the exchange's value from `ISecurityHeadersFeature.Nonce` and stamp it on
  their inline `<script>` and `<style>` elements.
- Endpoint overrides, as convention verbs on mapped routes and route groups:
  `WithSecurityHeaders(policy => ...)` adjusts a copy of the pipeline's policy,
  `WithSecurityHeaders(policy)` replaces it, and `DisableSecurityHeaders()` turns the fields off.
  Responses that never reach an endpoint keep the pipeline's policy.

A field the application set itself is never overwritten, unless
`SecurityHeadersPolicy.OverwriteExistingHeaders` says so. An application that sets `X-Frame-Options`,
or a `Content-Security-Policy` with a `frame-ancestors` directive, owns framing, and the middleware
then emits neither framing field.

Configuration lives on the `Add` verb only ("Add configures, Use places"). `UseSecurityHeaders()` takes
no arguments: it places the middleware `AddSecurityHeaders` registered (the last registration when there
are several), or one with the safe defaults when the application registered none. An undefined
enumeration value throws where the headers are registered, never per request.

## Usage

```csharp
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Services.AddRouting();
builder.Services.AddSecurityHeaders(headers =>
{
    headers.Options.ContentSecurityPolicy = ContentSecurityPolicy.Create(csp => csp
        .DefaultSrc(sources => sources.Self())
        .ScriptSrc(sources => sources.Nonce().StrictDynamic())
        .ObjectSrc(sources => sources.None())
        .BaseUri(sources => sources.None()));
    headers.Options.CrossOriginOpenerPolicy = CrossOriginOpenerPolicy.SameOrigin;
    headers.Options.PermissionsPolicy = PermissionsPolicy.Create(permissions => permissions
        .Disable("camera")
        .Disable("microphone")
        .AllowSelf("geolocation"));
});
WebApplication app = builder.Build();

// At the front of the pipeline (only UseHttpLogging goes ahead of it) and ahead of the exception
// boundary: every response passes through it. The server stages fields at head commit, including
// on an error page produced by a boundary on either side of this middleware.
app.UseSecurityHeaders();

app.UseErrorHandling();
IRouterBuilder routes = app.UseRouting();

// MapGet is the Web.Api endpoint verb; any mapped route or route group takes the override verbs.
routes.MapGet("/", async context =>
{
    string nonce = context.Features.Get<ISecurityHeadersFeature>()!.Nonce;
    // ... write <script nonce="{nonce}">...</script> into the page ...
});

// Partners may frame this one page; everything else stays unframeable.
routes.MapGet("/embed", EmbedAsync)
    .WithSecurityHeaders(policy => policy.Framing = FramingPolicy.AllowFrom("https://partner.example.com"));
```

## Dependencies

- `Assimalign.Cohesion.Web`: the pipeline abstractions `UseSecurityHeaders` extends and the
  `IWebApplicationMiddleware` contract `AddSecurityHeaders` registers.
- `Assimalign.Cohesion.Web.Routing`: endpoint metadata and the convention-builder seam the override
  verbs extend.
- `Assimalign.Cohesion.Http`: the header keys, the feature collection, and the Structured Field
  toolkit `Permissions-Policy` is serialized with.
- `Assimalign.Cohesion.Web.Server`: the response-starting contract covering buffered and streamed heads.

The default Web.Hosting server and Web.Testing publish the required starting feature. A custom server
must implement `IWebResponseStartingFeature`; otherwise the middleware throws at request time.
The callback registers the security-policy fields for preservation on a refused head's buffered
replacement, including application-owned values kept by no-clobber, without running again.

No DI, configuration, or logging dependency: `AddSecurityHeaders` is generated into the application
from the package's component-integration declaration, the policy is captured and compiled when it is
registered (with the provider overload, when the pipeline is composed), and the middleware resolves
nothing per request. Delivered to applications through the `App.Web` shared framework (via
`Sdk.Web`), with no project wiring required. `docs/DESIGN.md` records when the fields are written, the
no-clobber and framing rules, the override model, the nonce, the registration, and the non-goals. HSTS
is not part of this package; it is `AddHsts`/`UseHsts` in `Assimalign.Cohesion.Web.HttpsPolicy`.
