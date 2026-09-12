# Example landing zone — single topology

Each domain is one application: Identity, Networking, Platform, AppA, AppB and AppC.
Its Directory.Build.props writes the application identity for every project beneath it.
The root Gateway/Example.Gateway composes the six domain gateways as an application set.

| Area | Application / namespace | Projects | Gateway |
| --- | --- | --- | --- |
| `Platform/` | `platform` — deployed first, root of trust | `Example.Platform.SecretStore`, `Example.Platform.ConfigurationStore`, `Example.Platform.LogSpace` | `Example.Platform.Gateway` |
| `Identity/` | `identity` | `Example.Identity.IdentityHub` | `Example.Identity.Gateway` |
| `Networking/` | `networking` | `Example.Networking.Rezolvr` (DNS), `Example.Networking.VpnGateway` | `Example.Networking.Gateway` |
| `Zones/AppA` | `appa` | `Example.AppA.Api`, `Example.AppA.Spa`, `Example.AppA.Database` (schema in C#), `Example.AppA.SecretStore` | `Example.AppA.Gateway` |
| `Zones/AppB` | `appb` | same shape (billing) | `Example.AppB.Gateway` |
| `Zones/AppC` | `appc` | same shape (inventory) | `Example.AppC.Gateway` |
| `Gateway/` | `cohesion-system` | — | `Example.Gateway` — the **application set**: one gateway instance over all six applications, the sole production owner |

Every resource is an ordinary executable with Program.cs and explicitly enables orchestration,
because a gateway or sibling references it. The SDK generates its manifest, typed Resource
accessors and area-owned default control plane. Database programs define their schemas in C#.
Gateway projects inherit their enabled state from Sdk.Gateway.

The root and Networking gateways select Local. Networking's VPN data plane is not composable.
All other gateways select Local and InProcess. The zone programs retain the example's
Database/API/SPA realization subset; their SecretStore references remain declared.
Docker and Kubernetes are future provider choices, not included in this scaffold.
The appsettings files retain the intended single-cluster placement for that future integration.

```bash
dotnet build Example.K8s.slnx
dotnet run --project Zones/AppA/Example.AppA.Gateway -- --gateway local --mode describe
dotnet run --project Zones/AppA/Example.AppA.Gateway -- --gateway local --mode run
dotnet run --project Zones/AppA/Example.AppA.Gateway -- --gateway inprocess --mode run
```

Cross-domain references become generated externals. Configure their endpoint bindings in the
domain gateway before running a complete environment. Local examples use development endpoints;
production placement, remote trust and domain-specific configuration require your environment's values.
Set the organization feed owner in nuget.config and the image registry in Directory.Build.props.

Choose `--topology federated` when each domain should have its own gateway without a root application set.
