# ApplicationModel.Gateway.InProcess

Runs a gateway's resources inside the gateway's own process. `InProcessInfrastructure` is the
`inprocess` infrastructure provider and the in-process transport the programs publish through:
`builder.UseInProcess(infrastructure)` sets both on an `ApplicationBuilder`.

Bind each resource to its program's entry point with `AddProgram(provider, name, main)`. Deploying
the resource runs the entry point on a background thread in an `AppEnvironmentScope`. The scope
names the gateway, the in-process manifest endpoint, loopback ports for the program's `http`
endpoint and control plane, and every setting as a configuration variable. The program's host
stops on the scope's shutdown, which disposing the infrastructure signals, the last deployed program
first.

See [docs/OVERVIEW.md](docs/OVERVIEW.md) for usage and [docs/DESIGN.md](docs/DESIGN.md) for the
decisions and open questions.
