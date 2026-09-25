# `cohesion-app` — one repo, one gateway, two resources

The two-person-company story is an API and a database using the implemented Local and InProcess gateways. Select Local to
run the resources as supervised processes, or InProcess to run both inside the gateway process. Both resources are
ordinary executables with a `Program.cs`, and each opts in to orchestration with one csproj line so `Acme.Gateway` can
reference it.

Each project's `Properties/launchSettings.json` selects environment `Local` for developer-machine
runs. Development uses strict deployed security. Use `dotnet run --no-launch-profile` when
selecting an environment through shell variables so launch settings do not override them.

```bash
dotnet run --project Acme.Gateway -- --gateway local --mode run      # two supervised processes
dotnet run --project Acme.Gateway -- --gateway inprocess --mode run  # one process; one ResourceContext per resource
dotnet run --project Acme.Api                                        # standalone executable
```

Docker and Kubernetes remain the target scale-up path, but their gateway-provider packages are not selected by this scaffold. Once a provider is released, add it to `CohesionGateways`; adding Kubernetes also requires a
container registry. The resources and their references do not change. Topology 0—`Acme.Api` with an embedded database and
no gateway—is the step below this scaffold and is not materialized here.

Set the organization feed owner in `nuget.config` and the image registry in `Directory.Build.props`.
