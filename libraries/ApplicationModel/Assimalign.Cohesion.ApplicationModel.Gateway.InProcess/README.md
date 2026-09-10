# ApplicationModel.Gateway.InProcess

`Assimalign.Cohesion.ApplicationModel.Gateway.InProcess` is the opt-in gateway that realizes a
Cohesion application inside one process. It invokes each enabled, composable member's registered
executable entry point under an isolated ambient `ResourceContext`, adopts the `IHost` built by
that member, and preserves the normal plan-driven ordering, readiness, liveness, and teardown
contracts.

Select the gateway with `--gateway inprocess`. A gateway project that hosts members in process must
also set:

```xml
<CohesionGatewayInProcess>true</CohesionGatewayInProcess>
```

That property is the sanctioned Composite boundary. It makes project-referenced members real
runtime references and permits the area runtime assemblies required by those members. Every
nested member must have `CohesionApplicationModel=enabled`, must be project-referenced, and must
publish `artifact.composable=true`; disabled executables, manifest-package references, images,
and non-composable kinds remain out-of-process only.

The package depends on `Assimalign.Cohesion.ApplicationModel.Gateway`, plain
`Assimalign.Cohesion.Hosting`, and `Assimalign.Cohesion.Hosting.Resources`. It does not reference
an area runtime. Resource-area implementations remain unaware of orchestration; their generated
registration and ordinary `Program.Main` form the bridge.

See [docs/OVERVIEW.md](docs/OVERVIEW.md) for the project guide and
[docs/DESIGN.md](docs/DESIGN.md) for lifecycle, isolation, remapping, and NativeAOT invariants.
