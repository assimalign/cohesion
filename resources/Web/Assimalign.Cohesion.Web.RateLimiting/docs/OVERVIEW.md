# Assimalign.Cohesion.Web.RateLimiting — Overview

Inbound request rate limiting for the Cohesion Web pipeline. The package adapts the BCL
`System.Threading.RateLimiting` engine to the Web middleware model: it supplies the middleware, the
policy model, the partition-key surface, and the rejection response — it never reimplements a limiter
algorithm. All four BCL limiters (fixed window, sliding window, token bucket, concurrency), plus
partitioning and queueing, are available through the policy model.

## Scope

- **A global limiter** (`RateLimitingOptions.GlobalPolicy`) applied to every request, acquired up-front
  with the limiter's full queueing semantics. This is the primary flood shield.
- **Named policies** (`options.AddPolicy(name, policy)`) attached to individual endpoints through the
  sealed `RateLimitingMetadata` carrier in the routing metadata bag. A per-endpoint policy is evaluated
  *in addition to* the global limiter, read from the endpoint `UseRouting` publishes and acquired
  asynchronously, so queueing limiters work per endpoint too.
- **Partitioned limiting** with AOT-safe partition-key selectors: `RateLimitPartitionKeys.ClientAddress`
  (composing the forwarded-headers trust model), `RateLimitPartitionKeys.Header`, and any typed selector
  delegate.
- **Rejection handling**: `429 Too Many Requests` (configurable status) with a `Retry-After` header from
  the lease metadata, an `OnRejected` hook that may own the response, and an `OnDecision` observation hook.
- **A typed feature** (`IRateLimitingFeature`) exposing the decision (policy name, whether acquired,
  retry-after) to downstream stages.

## Dependencies

- `Assimalign.Cohesion.Web` — the pipeline builder and middleware abstractions the verb and middleware build on.
- `Assimalign.Cohesion.Web.Routing` — the published route match and endpoint-metadata bag the per-endpoint gate reads, and the acknowledgement routing checks before it dispatches.
- `Assimalign.Cohesion.Http` — the HTTP context, status codes, and header keys.
- `Assimalign.Cohesion.Http.Forwarded` — the `EffectiveRemoteIp` read the client-address partition key composes with.
- `Assimalign.Cohesion.Http.Streaming` — the response-streaming feature the rejection writer checks before answering.
- `System.Threading.RateLimiting` (BCL, AOT-safe) — the limiter engine.

It never references `Assimalign.Cohesion.Web.Hosting` (the resource hosting-isolation rule, `COHRES001`).

## Usage

```csharp
using System.Threading.RateLimiting;

using Assimalign.Cohesion.Web.RateLimiting;
using Assimalign.Cohesion.Web.Routing;
using Assimalign.Cohesion.Web.Routing.Metadata;

IRouterBuilder routes = app.UseRouting();

// A global limiter partitioned by the effective client address, plus a tighter named
// policy for an expensive endpoint. Registered after UseRouting, so the endpoint is known.
app.UseRateLimiting(options =>
{
    options.GlobalPolicy = RateLimitingPolicy.Create(context =>
        RateLimitPartition.GetFixedWindowLimiter(
            RateLimitPartitionKeys.ClientAddress(context),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 100,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));

    options.AddPolicy("expensive", RateLimitingPolicy.Create(context =>
        RateLimitPartition.GetConcurrencyLimiter(
            RateLimitPartitionKeys.ClientAddress(context),
            _ => new ConcurrencyLimiterOptions { PermitLimit = 5, QueueLimit = 10 })));

    options.OnRejected = async (rejection, cancellationToken) =>
    {
        // Optional: own the response. The default is a bodyless 429 + Retry-After.
    };
});

// Attach the named policy where the endpoint is mapped, or to every route of a group.
app.MapGet("/report", BuildReportAsync).RequireRateLimiting("expensive");

IRouterGroupBuilder admin = app.MapGroup("/admin").RequireRateLimiting("expensive");
admin.MapGet("ping", PingAsync).DisableRateLimiting();   // exempt one route of the group

// The verbs append RateLimitingMetadata; attaching it through the route's metadata is equivalent.
routes.Map(new Route(
    HttpMethod.Get,
    "/export",
    new RouterRouteHandler(ExportAsync),
    new RouterRouteMetadataCollection(new RateLimitingMetadata("expensive"))));
```

Register `UseRateLimiting` after `UseForwardedHeaders` (so client-address keys see the effective client
identity) and after `UseRouting` (so the request's endpoint and its `RateLimitingMetadata` are known when
it runs). Registered ahead of `UseRouting`, the global limiter still applies, but an endpoint that names a
policy fails with `InvalidOperationException` when it is dispatched instead of running without its limit.

See `docs/DESIGN.md` for the policy model, the partition-key trust posture, the per-endpoint metadata
mechanics, the queueing and disposal posture, the telemetry follow-up, and the non-goals.
