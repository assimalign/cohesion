# ApplicationModel.Gateway.InProcess

Runs a gateway's resources inside the gateway's own process. `InProcessInfrastructure` is the
gateway half of the in-process platform: the `inprocess` infrastructure provider, whose channel the
programs publish through, and the instance client the gateway calls them with.
`builder.UseInProcess(infrastructure)` sets both on an `ApplicationGatewayBuilder`; each program opts
into the platform with `UseInProcess()` from ApplicationModel.Hosting.InProcess.

Bind each resource to its program's entry point with `AddProgram(provider, name, main)`. Deploying
the resource runs the entry point on a background thread in an `AppEnvironmentScope`. The scope
names the platform, the in-process endpoint, a loopback port for the program's `http` endpoint,
and every setting as a configuration variable. The program's host
stops on the scope's shutdown, which disposing the infrastructure signals, the last deployed program
first.

See [docs/OVERVIEW.md](docs/OVERVIEW.md) for usage and [docs/DESIGN.md](docs/DESIGN.md) for the
decisions and open questions.
