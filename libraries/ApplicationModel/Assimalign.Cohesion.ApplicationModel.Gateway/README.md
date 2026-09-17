# Assimalign.Cohesion.ApplicationModel.Gateway

The shared plan-driven gateway lifecycle and the Local process gateway. The portable
ApplicationModel package owns declarations and plans; this package realizes them through
explicit controllers, observed state, protected input resolution, and dependency admission.

`LocalGateway` launches the customer's real resource executable. `ApplicationGateway`
provides the shared controller, observation, reconciliation and plan-derived readiness
algorithm used by gateway implementations. Discovery exports and authenticated command
delivery use the portable contracts; platform compilers remain platform-owned.

## Boundaries

- Kubernetes and Docker gateways live in cohesion-platforms and consume `ResourcePlan`.
- `Gateway.InProcess` owns composite member invocation and ambient resource scopes.
- `Gateway.ControlPlane` owns the authenticated application control-plane endpoints.
- Thin store clients resolve protected mounts; this package does not reference resource
  hosting runtimes. Telemetry injection uses observed LogSpace endpoints and scoped tokens.

## Consumer entry point

An `Sdk.Gateway` consumer keeps its real `Program.cs`:

```csharp
using Assimalign.Cohesion.ApplicationModel;

IApplicationBuilder builder = Gateway.CreateBuilder(args);
builder.AddAllResources();
builder.UseGateway(args);
await builder.Build().RunAsync();
```

See the [package design](docs/DESIGN.md), [overview](docs/OVERVIEW.md), and
[ApplicationModel design v3](../../../docs/libraries/ApplicationModel/DESIGN.md) for ownership and current limitations.
The [realization plan](../../../docs/programs/REALIZATION_PLAN.md) and
[runtime contract](../../../docs/RUNTIME_CONTRACT.md) own the wire contracts.
