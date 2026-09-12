# Web.ControlPlane

Install the resource protocol on a private Web application's pipeline:

```csharp
IWebApplicationPipelineBuilder pipeline = privateApplication;
pipeline.UseResourceControlPlane(controlPlane, resourceContext,
    () => resourceApplication.Context.State is HostState.Started);
```

Install before application middleware. The feature serves health, readiness, liveness, endpoint discovery, stop, and command envelopes. Gateway-managed namespaced routes authenticate against the published application trust key; standalone resources work without a gateway. Domain command handlers remain the owning area's responsibility.

See [DESIGN.md](DESIGN.md) for protocol, authentication, ownership, and parity constraints.
