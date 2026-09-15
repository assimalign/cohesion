# Web.Hosting.Resources

Install the resource protocol on a private Web application's pipeline:

```csharp
using Assimalign.Cohesion.Web.Hosting.Resources;

IWebApplicationPipelineBuilder pipeline = privateApplication;
pipeline.UseResourceControlPlane(controlPlane, resourceContext,
    () => resourceApplication.Context.State is HostState.Started);
```

Install before application middleware. The feature serves health, readiness, liveness, endpoint discovery, stop, and command envelopes. Gateway-managed namespaced routes authenticate against the published application trust key; standalone resources work without a gateway. Domain command handlers remain the owning area's responsibility.

This hosting-family library references the Web root, Hosting.Resources, Hosting.Health, and
IdentityModel.Token.JsonWebToken. It never references Web.Hosting. Roots and feature libraries
cannot reference it; other areas' hosting modules consume it privately. App.Web exposes it publicly.

See [DESIGN.md](DESIGN.md) for protocol, authentication, ownership, and parity constraints.
