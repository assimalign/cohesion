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
  *in addition to* the global limiter.
- **Partitioned limiting** with AOT-safe partition-key selectors: `RateLimitPartitionKeys.ClientAddress`
  (composing the forwarded-headers trust model), `RateLimitPartitionKeys.Header`, and any typed selector
  delegate.
- **Rejection handling**: `429 Too Many Requests` (configurable status) with a `Retry-After` header from
  the lease metadata, an `OnRejected` hook that may own the response, and an `OnDecision` observation hook.
- **A typed feature** (`IRateLimitingFeature`) exposing the decision (policy name, whether acquired,
  retry-after) to downstream stages.

## Dependencies

- `Assimalign.Cohesion.Web` — the pipeline builder and middleware abstractions the verb and middleware build on.
- `Assimalign.Cohesion.Web.Routing` — the route-match feature and endpoint-metadata bag the per-endpoint gate reads.
- `Assimalign.Cohesion.Http` — the HTTP context, status codes, and header keys.
- `Assimalign.Cohesion.Http.Forwarded` — the `EffectiveRemoteIp` read the client-address partition key composes with.
- `Assimalign.Cohesion.Http.Streaming` — the response-streaming feature the rejection writer checks before answering.
- `System.Threading.RateLimiting` (BCL, AOT-safe) — the limiter engine.

It never references `Assimalign.Cohesion.Web.Hosting` (the resource hosting-isolation rule, `COHRES001`).

## Usage

```csharp
using System.Threading.RateLimiting;

using Assimalign.Cohesion.Web.RateLimiting;

// A global limiter partitioned by the effective client address, plus a tighter named
// policy for an expensive endpoint.
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

// Attach the named policy to an endpoint through routing metadata (Map time).
routes.Map(new Route(
    HttpMethod.Get,
    "/report",
    new RouterRouteHandler(BuildReportAsync),
    new RouterRouteMetadataCollection(new RateLimitingMetadata("expensive"))));
```

Register `UseRateLimiting` early — after `UseForwardedHeaders` (so client-address keys see the effective
client identity) and before `UseRouting` (so it can gate matched endpoints at the route-match seam).

See `docs/DESIGN.md` for the policy model, the partition-key trust posture, the per-endpoint metadata
mechanics, the queueing and disposal posture, the telemetry follow-up, and the non-goals.
