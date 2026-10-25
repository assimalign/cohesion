# Assimalign.Cohesion.Web.Cors — Overview

Cross-origin resource sharing (CORS) for the Cohesion Web pipeline, following the Fetch standard's CORS
protocol. `UseCors` answers CORS preflights and adds the CORS headers to the responses its policy governs, so
a browser lets an allowed origin's script read them. Policies are immutable, validated when they are built,
and selected per endpoint from routing metadata.

## Scope

- **A policy model** (`CorsPolicy`, built with `CorsPolicyBuilder`): allowed origins (an exact list, a
  predicate, or any origin), methods, request headers, exposed response headers, credentials, and the
  preflight max age. Configured origins must be origins (`scheme://host[:port]`) and are normalized to the
  form a browser sends; any origin cannot be combined with credentials. `WithMethods` requires RFC 9110
  tokens of at most 32 characters, matching routing and the preflight parser; invalid values throw when added.
- **The middleware** (`UseCors`): a preflight is answered `204` with the policy's grant (origin, credentials,
  methods, headers, max age) or with no CORS header at all when the policy denies it, and never reaches the
  endpoint. An actual request gets the allowed origin (echoed, or `*`), credentials and exposed headers, plus
  `Vary: Origin` whenever the answer depends on the origin.
- **Fetch matching**: methods compare byte for byte, with the CORS-safelisted `GET`, `HEAD` and `POST`
  always allowed; request-header names compare case-insensitively.
- **Per-endpoint policy selection**: a named policy, an inline policy, or `DisableCors`, attached with
  convention verbs to a route or a group and read last-wins from the endpoint `UseRouting` publishes; the
  default policy covers everything else.
- **Opt-in live configuration** (`CorsPolicyStore`, `UseDynamicCors`): a declarative default and named
  policy set can be validated and replaced atomically while requests continue. `CorsJsonContext`
  supplies reflection-free JSON metadata for the configuration contract.

## Dependencies

- `Assimalign.Cohesion.Web`: the pipeline builder and middleware abstractions.
- `Assimalign.Cohesion.Web.Routing`: the published endpoint and its metadata, the preflight's candidate
  endpoint, the endpoint convention builders, and the acknowledgement routing checks before it dispatches.
- `Assimalign.Cohesion.Http`: the HTTP context, header keys and methods.
- `Assimalign.Cohesion.Web.Server`: the response-starting contract for buffered and streamed heads.

It never references `Assimalign.Cohesion.Web.Hosting` or any `Assimalign.Cohesion.Hosting*` library
(`COHRES001`, `COHRES004`).

## Usage

```csharp
using Assimalign.Cohesion.Web.Cors;
using Assimalign.Cohesion.Web.Routing;

IRouterBuilder routes = app.UseRouting();

// After UseRouting, ahead of authorization, rate limiting, antiforgery and output caching.
app.UseCors(options =>
{
    // Every endpoint without CORS metadata: the single-page application's origin.
    options.AddDefaultPolicy(policy => policy
        .WithOrigins("https://app.example")
        .WithMethods("PUT", "DELETE")
        .WithHeaders("Content-Type", "Authorization")
        .WithExposedHeaders("ETag")
        .AllowCredentials()
        .SetPreflightMaxAge(TimeSpan.FromMinutes(10)));

    // A named policy endpoints opt into.
    options.AddPolicy("partners", policy => policy
        .SetIsOriginAllowed(origin => origin.StartsWith("https://", StringComparison.Ordinal)
            && origin.EndsWith(".partners.example", StringComparison.Ordinal)));
});

app.MapGet("/catalog", GetCatalogAsync).RequireCors("partners");

IRouterGroupBuilder admin = app.MapGroup("/admin").DisableCors();   // same-origin only
admin.MapGet("status", GetStatusAsync).RequireCors(policy => policy.WithOrigins("https://ops.example"));
```

Register `UseCors` after `UseRouting`, so the endpoint's policy is known, and ahead of any middleware that
can reject a preflight. Also register it before `UseOutputCache`, so cache hits register the starting
callback that writes their CORS grant. An endpoint that declares CORS fails with `InvalidOperationException` when it is
dispatched without `UseCors` having processed it (missing, or registered ahead of `UseRouting`), instead of
running under the wrong policy. A preflight for a method no route serves is answered by the default policy,
or left to routing's `405` when there is none.

Actual-response grant headers are applied when the response head commits. An exception boundary on either
side of `UseCors` can replace a failed response and keep its CORS headers. The default Web.Hosting server
and Web.Testing publish the required `IWebResponseStartingFeature`; a custom server must implement it.
The callback registers its grant fields and `Vary` for preservation, so a refused head's buffered
replacement retains the final CORS decision without running the callback again.

See [`DESIGN.md`](DESIGN.md) for the policy model and its validation, policy selection, the preflight's
policy source, the `Vary: Origin` and cache rules, the fail-closed decision, ordering, and the non-goals.

## Typed configured header names

Individual string arguments still work: `policy.WithHeaders("Content-Type", "X-Tenant")` and
`policy.WithExposedHeaders("ETag")` convert to `HttpFieldName`. To pass a prebuilt array, use
`HttpFieldName[]` instead of `string[]`. The immutable policy's `Headers` and `ExposedHeaders`
now contain `HttpFieldName`; use `.Value` for their text. Names compare ordinally without case,
and CORS continues to reject `*` and invalid or uninitialized names.

## Replace policies while the application runs

Use one store per application and share it with the composition root's management handler:

```csharp
CorsPolicyStore cors = CorsPolicyStore.Create(new CorsConfiguration
{
    DefaultPolicy = new CorsPolicyConfiguration
    {
        Origins = ["https://app.example"],
        Methods = ["PUT", "DELETE"],
        Headers = ["Content-Type", "Authorization"],
        AllowCredentials = true,
        PreflightMaxAgeSeconds = 600,
    },
});

app.UseRouting();
app.UseDynamicCors(cors);

// Read from an authenticated management GET handler:
byte[] current = JsonSerializer.SerializeToUtf8Bytes(
    cors.GetConfiguration(), CorsJsonContext.Default.CorsConfiguration);

// Deserialize a complete replacement in its PUT handler, then validate and publish:
CorsConfiguration replacement = JsonSerializer.Deserialize(
    requestBody, CorsJsonContext.Default.CorsConfiguration)
    ?? throw new InvalidOperationException("A CORS configuration is required.");
cors.Replace(replacement);
```

`CorsPolicyStore` owns policy state; the executable owns management registration, authorization and
transport. No endpoint is opened by this package. `GetConfiguration()` returns a detached normalized
copy, and mutating input or returned objects never changes the running pipeline. `Replace` validates
every policy before publication; a failure preserves the previous configuration. Requests already
processing a response keep the policy they selected, and subsequent requests select the replacement.

Replacement controls the default and named policy set. Existing named policies must remain present
so routes referencing them continue to resolve; new names can be added. The default policy can be
removed; requests without endpoint CORS metadata then have grant headers removed at response commit,
including grants replayed by an output cache. Inline `RequireCors(policy)` policies and `DisableCors()`
retain their precedence, and disabled endpoints can continue managing their own CORS headers.
Executable origin predicates remain in static `UseCors` configuration because a delegate cannot be
round-tripped as JSON. Static `UseCors` behavior is unchanged.

Configuration is process-local. The management host must own persistence and replica coordination
when those are required. Already cached browser preflight grants remain usable until their cache
lifetime expires; changing the server's policy cannot revoke those cached grants immediately.
