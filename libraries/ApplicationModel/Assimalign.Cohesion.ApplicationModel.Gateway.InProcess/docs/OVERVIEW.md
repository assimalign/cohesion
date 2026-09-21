# Gateway.InProcess overview

`Assimalign.Cohesion.ApplicationModel.Gateway.InProcess` realizes enabled, composable Cohesion
resources as independently owned hosts inside one gateway process. It preserves the ordinary
application model, plan validation, dependency gate, resource control planes, and observed-state
contract while replacing operating-system child processes with ambient entry-point invocations.

The package depends only on `ApplicationModel`, `ApplicationModel.Gateway`, plain `Hosting`, and
`Hosting.Resources`. It does not reference a resource area's `*.Hosting` package; the consuming
Composite obtains those runtimes through enabled project references when
`CohesionGatewayInProcess=true`.

Select the provider with `--gateway inprocess`. The generated `Gateway.CreateBuilder(args)` binds
the same-application closure of enabled, composable project resources by manifest identity, and
each generated `Add<Name>()` verb binds the descriptor it returns, so a resource is colocatable
whether it was added through the generated verb, the area verb over `Manifests.<Name>`, or a
third-party application model's verb. Each member receives a resource-specific content root,
persisted loopback endpoints, resolved mounts and credentials, and observed dependency addresses
through its ambient `ResourceContext`.

`--mode render` emits the same versioned local plan-set envelope as the Local gateway, with each
resource compiled to an `inProcessHost` unit. It is offline: generated entry points are not invoked,
runtime inputs are not resolved, and the state directory is not created.

See [DESIGN.md](DESIGN.md) for lifecycle, restart, remapping, and NativeAOT decisions and
[Assembly](Assembly/Assimalign.Cohesion.ApplicationModel.Gateway.InProcess/OVERVIEW.md) for the
public API map.

Declared resource commands use the adopted host's registered control plane directly after
Running. This retains the generic gateway's ownership, observation, and teardown behavior while
avoiding a second control-plane instance for an in-process member.
