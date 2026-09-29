# Example landing zone — federated topology

Identity, Networking, Platform, AppA, AppB and AppC each own an application and gateway.
This tree shares the single topology's projects and programs, with no root Gateway/ application set.
Each domain's Directory.Build.props writes its CohesionApplication explicitly.

Every resource enables orchestration and generates a manifest, typed Resource accessors and its
area-owned default control plane. Gateways inherit their always-enabled SDK behavior.
Cross-domain references become generated externals; configure the consumer gateway's remote
bindings for your environment. The included programs retain the landed examples' developer-machine
endpoint bindings and zone Database/API/SPA realization subset.

Gateways compose their resources with the area verbs over the generated `Manifests` members, and
nothing is registered by convention. `Example.Platform.Gateway` references the SecretStore and
ConfigurationStore `ApplicationModel.Orchestration` packages and registers its SecretStore (the
`platform-secretstore:<key>` mount source, certificate authority and trust store), its ConfigurationStore
and its LogSpace telemetry sink in `Program.cs`. Cross-application store sources are a documented
follow-up, so Identity and Networking read their secrets from gateway parameters: `identity-hub-tls`
(a PEM bundle with the IdentityHub `https` certificate, its private key and chain) and
`identity-signing-keys` for the identity gateway, `networking-vpn-keys` for the networking gateway.
Set each with `cohesion parameter set <name> --stdin --project <gateway>`, which writes the encrypted
`.cohesion/<application>/parameters.json` that `cohesion run --project <gateway>` reads, or pass
`--parameter name=value` to the gateway. An unset parameter leaves its resource unrealized with an
error naming it.

The appsettings files describe the intended multi-cluster placement: Platform on cluster-03,
Identity on cluster-01, Networking on cluster-02 and zones on cluster-04. Current providers
are Local and InProcess, with Networking restricted to Local for its non-composable VPN data plane.
Kubernetes providers and production trust configuration are separate integration work.

Every project has `Properties/launchSettings.json` selecting environment `Local` for
developer-machine runs. The zone APIs keep local overrides in `appsettings.Local.json`.
Development uses strict deployed security. To supply an environment through shell variables,
use `dotnet run --no-launch-profile` so launch settings do not override those values.

```bash
dotnet build Example.Federated.slnx
dotnet run --project Platform/Example.Platform.Gateway -- --gateway local --mode describe
dotnet run --project Networking/Example.Networking.Gateway -- --gateway local --mode describe
dotnet run --project Zones/AppA/Example.AppA.Gateway -- --gateway local --mode run
dotnet run --project Zones/AppA/Example.AppA.Gateway -- --gateway inprocess --mode run
```

Set the organization feed owner in nuget.config and the image registry in Directory.Build.props.
Choose `--topology single` to add a root application set over the same six domains.
