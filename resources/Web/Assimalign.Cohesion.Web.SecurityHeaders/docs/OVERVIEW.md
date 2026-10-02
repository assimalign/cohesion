# Assimalign.Cohesion.Web.SecurityHeaders — Overview

Browser security headers for the Cohesion Web pipeline. One verb, `UseSecurityHeaders`, puts safe
defaults on every response that passes through it and lets an application opt into a Content Security
Policy (with per-request nonces), a Permissions Policy, and the cross-origin isolation fields. Endpoints
can adjust, replace or disable the policy through routing metadata.

## What it provides

- `UseSecurityHeaders(Action<SecurityHeadersPolicy>?)`: a pipeline verb on
  `IWebApplicationPipelineBuilder`. With no configuration it emits:
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

## Usage

```csharp
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.AddRouting();
WebApplication app = builder.Build();

// At the front of the pipeline (only UseHttpLogging goes ahead of it) and ahead of the exception
// boundary: every response passes through it, and the fields are staged after the boundary has
// written an error page.
app.UseSecurityHeaders(policy =>
{
    policy.ContentSecurityPolicy = ContentSecurityPolicy.Create(csp => csp
        .DefaultSrc(sources => sources.Self())
        .ScriptSrc(sources => sources.Nonce().StrictDynamic())
        .ObjectSrc(sources => sources.None())
        .BaseUri(sources => sources.None()));
    policy.CrossOriginOpenerPolicy = CrossOriginOpenerPolicy.SameOrigin;
    policy.PermissionsPolicy = PermissionsPolicy.Create(permissions => permissions
        .Disable("camera")
        .Disable("microphone")
        .AllowSelf("geolocation"));
});

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

- `Assimalign.Cohesion.Web`: the pipeline abstractions the verb extends.
- `Assimalign.Cohesion.Web.Routing`: endpoint metadata and the convention-builder seam the override
  verbs extend.
- `Assimalign.Cohesion.Http`: the header keys, the feature collection, and the Structured Field
  toolkit `Permissions-Policy` is serialized with.
- `Assimalign.Cohesion.Http.Streaming`: the response-streaming feature, which the middleware stands in
  for so a streamed response carries the fields too.

No DI, configuration, or logging dependency: the policy is captured and compiled when the verb is
called, and the middleware resolves nothing per request. Delivered to applications through the
`App.Web` shared framework (via `Sdk.Web`), with no project wiring required. `docs/DESIGN.md` records
when the fields are written, the no-clobber and framing rules, the override model, the nonce, and the
non-goals. HSTS is not part of this package; it is `UseHsts` in `Assimalign.Cohesion.Web.HttpsPolicy`.
