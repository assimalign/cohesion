# Example landing zone — federated topology

Identity, Networking, Platform, AppA, AppB and AppC each own an application and gateway.
This tree shares the single topology's projects and programs, with no root Gateway/ application set.
Each domain's Directory.Build.props writes its CohesionApplication explicitly.

Every resource enables orchestration and generates a manifest, typed Resource accessors and its
area-owned default control plane. Gateways inherit their always-enabled SDK behavior.
Cross-domain references become generated externals; configure the consumer gateway's remote
bindings for your environment. The included programs retain the landed examples' development
endpoint bindings and zone Database/API/SPA realization subset.

The appsettings files describe the intended multi-cluster placement: Platform on cluster-03,
Identity on cluster-01, Networking on cluster-02 and zones on cluster-04. Current providers
are Local and InProcess, with Networking restricted to Local for its non-composable VPN data plane.
Kubernetes providers and production trust configuration are separate integration work.

```bash
dotnet build Example.Federated.slnx
dotnet run --project Platform/Example.Platform.Gateway -- --gateway local --mode describe
dotnet run --project Networking/Example.Networking.Gateway -- --gateway local --mode describe
dotnet run --project Zones/AppA/Example.AppA.Gateway -- --gateway local --mode run
dotnet run --project Zones/AppA/Example.AppA.Gateway -- --gateway inprocess --mode run
```

Set the organization feed owner in nuget.config and the image registry in Directory.Build.props.
Choose `--topology single` to add a root application set over the same six domains.
