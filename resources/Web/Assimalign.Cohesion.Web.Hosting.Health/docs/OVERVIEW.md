# Assimalign.Cohesion.Web.Hosting.Health

The hosting-family adapter extends `IHealthChecksBuilder` with `AddContributor`.
It references `Assimalign.Cohesion.Web.Health` and `Assimalign.Cohesion.Hosting.Health`.
The Web health model and HTTP endpoints remain in `Web.Health`.

## Usage


```csharp
using Assimalign.Cohesion.Web.Health;
using Assimalign.Cohesion.Web.Hosting.Health;

IHealthCheckService health = HealthChecks.CreateBuilder()
    .AddContributor(contributor)
    .Build();
pipeline.MapHealthChecks(health);
```

The contributor name identifies the check. Omitted tags include readiness and liveness;
explicit tags replace those defaults. Failure status and timeout use Web health policy.
The application owns contributor lifetime; the adapter only evaluates it.

`App.Web` exposes this package. `Database.Hosting` consumes it through a private project
reference paired with the adapter assembly in the `App.Database` runtime pack. See [Design](DESIGN.md) for the dependency and cancellation contracts.
