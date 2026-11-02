# ApplicationModel.Gateway.InProcess

Runs a gateway's resources inside the gateway's own process. `InProcessInfrastructure` is the
gateway half of the in-process platform: the `inprocess` infrastructure provider, whose channel the
programs publish through, and the instance client the gateway calls them with.
`builder.UseInProcess()` sets both on an `ApplicationGatewayBuilder` with infrastructure the gateway
builds and owns; `builder.UseInProcess(infrastructure)` sets infrastructure the caller owns. Each
program opts into the platform with `UseInProcess()` from ApplicationModel.Hosting.InProcess.

A gateway on Sdk.Gateway binds each resource it references with `CohesionResource="<name>"` on the
project reference: the build registers the program's entry point, top-level statements included.
`AddProgram(provider, name, main)` binds one explicitly, on the builder or the infrastructure. Deploying
the resource runs the entry point on a background thread in an `AppEnvironmentScope`. The scope
names the platform, the in-process endpoint, a loopback port for the program's `http` endpoint,
and every setting as a configuration variable. The program's host
stops on the scope's shutdown, which disposing the infrastructure (or the gateway that owns it) signals, the last deployed program
first.

See [docs/OVERVIEW.md](docs/OVERVIEW.md) for usage and [docs/DESIGN.md](docs/DESIGN.md) for the
decisions and open questions.
