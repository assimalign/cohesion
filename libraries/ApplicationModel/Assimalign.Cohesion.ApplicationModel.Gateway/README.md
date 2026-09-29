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
- This package references nothing under `resources/`. Store-backed mounts, source-free endpoint
  certificates, trusted issuers, command inputs, and the telemetry sink come only from the
  providers the application registers in `builder.Providers` (for example through
  `Assimalign.Cohesion.SecretStore.ApplicationModel.Orchestration`'s `builder.UseSecretStore(store)`),
  or, for an application-set member, in the member's own registration callback
  (`set.AddApplication(Applications.Platform, platform => platform.UseSecretStore("platform-secrets"))`).
  Without them the development certificate authority and local trust file are Local-only, and no
  telemetry is injected. Credentials go through `builder.Providers.CredentialIssuer` first and fall
  back to the default ES256 application-key issuer.

## Consumer entry point

An `Sdk.Gateway` consumer keeps its real `Program.cs` and names what it composes, one
hand-written call per resource: the generated `Manifests.<Name>` member goes to the verb of the
area's ApplicationModel package, or to `AddResource` for a kind that has none.

```csharp
using Assimalign.Cohesion.ApplicationModel;

IApplicationBuilder builder = Gateway.CreateBuilder(args);
builder.AddWeb(Manifests.Api, new WebResourceOptions { Replicas = 2 }); // the Web area's verb
builder.AddResource(Manifests.Worker);                                   // no ApplicationModel in reach
builder.UseGateway(args);
await builder.Build().RunAsync();
```

See the [package design](docs/DESIGN.md), [overview](docs/OVERVIEW.md), and
[ApplicationModel design v3](../../../docs/libraries/ApplicationModel/DESIGN.md) for ownership and current limitations.
The [realization plan](../../../docs/programs/REALIZATION_PLAN.md) and
[runtime contract](../../../docs/RUNTIME_CONTRACT.md) own the wire contracts.
