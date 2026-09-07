# Hosting.Resources

`Assimalign.Cohesion.Hosting.Resources` is the opt-in resource runtime for Cohesion executables.
It owns the ambient `ResourceContext`, protected mounts, control-plane aggregation, compiler-rooted
entry invocation, and the runner that translates a plain host lifetime into the supervisor-facing
resource process contract.

The package directly depends on `Assimalign.Cohesion.Core`, `Assimalign.Cohesion.Hosting`, and
`Assimalign.Cohesion.Hosting.Health`. It also uses the Windows
`System.Security.Cryptography.ProtectedData` BCL facade for DPAPI-backed mounts.

The base `Assimalign.Cohesion.App` framework delivers Resources beside plain Hosting because every
opt-in resource SDK generates `ResourceRuntime` calls; Health follows as a dependency. Presence in
the framework is not activation: a disabled `CohesionApplicationModel` produces no resource
registration, while direct package consumers can choose their references independently.

Enabled resource generators register an executable entry and an area control-plane factory.
`ResourceRuntime.HostBuilt` attaches that invocation's control plane and installs a resource runner
on that host only. Ordinary hosts remain ordinary Hosting runs.

On the normal fully-started resource-process path the runner emits:

`cohesion-resource: ready` → `cohesion-resource: stopping` → `cohesion-resource: stopped`

If a stop is accepted while `OnStartedAsync` is still running, `ready` is omitted and the protocol
begins with `stopping`; a late `ready` is never emitted after the stop transition.

It converts the executable boundary to the frozen `cohesion/sysexits/v1` mapping: `0` success,
`64` configuration, `69` dependency, `70` other pre-ready failure, `75` other post-ready failure,
`130` interrupted drain, and `143` other requested-stop drain cancellation.
