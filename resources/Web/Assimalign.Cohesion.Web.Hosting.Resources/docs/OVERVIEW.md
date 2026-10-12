# Web.Hosting.Resources

Install the resource protocol on a private Web application's pipeline:

```csharp
using Assimalign.Cohesion.Web.Hosting.Resources;

IWebApplicationPipelineBuilder pipeline = privateApplication;
pipeline.UseResourceControlPlane(controlPlane, resourceContext,
    () => resourceApplication.Context.State is HostState.Started);
```

Install before application middleware. An optional trailing port gates every route; null serves every listener. Hosts composing manually retain `ResourceControlPlaneMiddleware.CreateLifetime(resourceContext)` while serving requests through `InvokeAsync(...)`, then dispose the lease after those requests drain. Stop uses Web.Server's response-completion feature to send its acknowledgement before requesting shutdown. The feature serves health, readiness, liveness, endpoint discovery, stop, and command envelopes. Gateway-managed namespaced routes authenticate against the published application trust key; standalone namespaced routes accept only transport peers at `127.0.0.1`, `::1`, or IPv4-mapped `127.0.0.1` and return 403 to other or unknown peers. Forwarded headers do not grant access. Bare `/healthz`, `/readyz`, and `/livez` stay public. Remote standalone management is no longer supported (a behavioral break from 10.0.0-preview.1, #1431). Domain command handlers remain the owning area's responsibility.

Managed middleware reuses one ECDSA verification handle while its lifetime leases are active
(#1412). `UseResourceControlPlane` transfers its lease to `IWebApplicationLifetime` on the pipeline's
application context; Web.Hosting supplies this capability and releases the lease during final
application disposal. A stop and restart keep it alive. A custom managed pipeline context without
this capability now fails composition, a behavior change from 10.0.0-preview.1; use the manual
lifetime API below or implement the generic lifetime seam. A disposed composed pipeline refuses
further invocation.

This hosting-family library references the Web root, Web.Server (for `IWebResponseCompletionFeature`,
#1379), Hosting.Resources, Hosting.Health, and IdentityModel.Token.JsonWebToken. It never references Web.Hosting. Roots and feature libraries
cannot reference it; other areas' hosting modules consume it privately. App.Web exposes it publicly.

See [DESIGN.md](DESIGN.md) for protocol, authentication, ownership, and parity constraints.

## Manual composition API

`ResourceControlPlaneMiddleware.Validate(ResourceContext resourceContext)` performs the verb's eager managed-identity validation. It rejects a null context, a blank managed resource name, a missing application identity or public trust key, and an invalid P-256 JWK. Standalone contexts need no identity.

`ResourceControlPlaneMiddleware.CreateLifetime(ResourceContext resourceContext)` performs the same
validation and returns an idempotent `IDisposable` lease. The first lease imports the verification
handle; the last disposal releases it. Multiple owners of the same resource context share that
handle, and verification is synchronized with disposal. Keep the lease across the manual host's
request loop:

```csharp
using IDisposable lifetime =
    ResourceControlPlaneMiddleware.CreateLifetime(resourceContext);

// exchanges is the host's request stream; end it only after accepting has stopped.
await foreach (IHttpContext exchange in exchanges)
{
    await ResourceControlPlaneMiddleware.InvokeAsync(
        controlPlane, resourceContext, isApplicationReady(), controlPlanePort, exchange, next);
}
// Every request above has completed before the lifetime scope ends.
```

`ResourceControlPlaneMiddleware.InvokeAsync(IResourceControlPlane controlPlane, ResourceContext resourceContext, bool isApplicationReady, int? controlPlanePort, IHttpContext context, WebApplicationMiddleware next)` returns a `Task` for one exchange. Required reference arguments cannot be null. A mismatched non-null port forwards every path to `next`; null applies no port gate. Unknown namespaced paths apply the gateway credential check or standalone loopback restriction before returning 404.

Acquire the lifetime during composition, then register `InvokeAsync` ahead of routes the terminal should own. The owning host supplies readiness and listener selection and drains concurrent requests before disposing the lease. Existing callers that only call `Validate` and `InvokeAsync` remain supported: they reuse the parsed key and validation profile but create and dispose a temporary ECDSA handle for each signed token. Stop registers a callback on `IWebResponseCompletionFeature` when present; the callback requests stop with `CancellationToken.None` after transmission. A custom server without the feature uses the request token for the direct-stop fallback.
