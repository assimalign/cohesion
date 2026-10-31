# ApplicationModel Library Area

> **Being rebuilt (2026-10-27).** Since 2026-10-29 the model is three projects: the shared
> [contracts](Assimalign.Cohesion.ApplicationModel/docs/DESIGN.md), the logical side in
> [Definition](Assimalign.Cohesion.ApplicationModel.Definition/docs/DESIGN.md), and the physical
> side in [Hosting](Assimalign.Cohesion.ApplicationModel.Hosting/docs/DESIGN.md), formerly
> `Assimalign.Cohesion.Hosting.Resources`. The [gateway](Assimalign.Cohesion.ApplicationModel.Gateway/docs/DESIGN.md)
> builds on the definitions. Each gateway runs a model of its own, deployed by its own pipeline, and
> a child gateway joins its parent. Since 2026-10-30 every transport belongs to a platform, which is
> a pair of packages: the in-process platform is
> [Hosting.InProcess](Assimalign.Cohesion.ApplicationModel.Hosting.InProcess/docs/DESIGN.md), which a
> resource opts into, and [Gateway.InProcess](Assimalign.Cohesion.ApplicationModel.Gateway.InProcess/docs/DESIGN.md),
> which runs a gateway's resources in its own process. No out-of-process platform exists yet.
> `Gateway.ControlPlane`, the retired gateway's HTTP front end, was removed on 2026-10-29. The
> linked area records describe the retired design.

The L2 application-composition and orchestration plane: the shared contracts, the definitions a
gateway program declares, and the instance side every area's `<Area>.Hosting.ApplicationModel`
package builds on, plus the gateway family that deploys and manages resources without loading their
runtimes.

The area's architecture record lives in
[docs/libraries/ApplicationModel/DESIGN.md](../../docs/libraries/ApplicationModel/DESIGN.md);
this README is the project map. The signed
[developer-experience design](../../docs/DEVELOPER_EXPERIENCE_DESIGN.md) is the direction of
record and wins on every conflict. The full reference graph for every Cohesion assembly is in
[docs/DEPENDENCIES.md](../../docs/DEPENDENCIES.md).

## Layering

In the repo's L1/L2/L3 model (see [docs/programs/DELIVERY_ROADMAP.md](../../docs/programs/DELIVERY_ROADMAP.md)),
this area is **L2 — application runtime and composition**: it sits above the L1 foundation
libraries (`Core`, `Hosting`) and below every L3 service platform under `resources/`.

## Project map

The arrow means "references": `Gateway.InProcess --> Gateway` reads "`Gateway.InProcess`
references `Gateway`". The logical side (Definition) and the physical side (Hosting) both reference
the shared contracts and never each other; the gateway packages layer onto the definitions. A
platform is a pair: its instance half references Hosting, and its gateway half references the
Gateway and the instance half, the one place the two sides meet. A resource references only the
instance half.

```mermaid
flowchart LR
    Definition["ApplicationModel.Definition"] --> Contracts["Assimalign.Cohesion.ApplicationModel"]
    Hosting["ApplicationModel.Hosting"] --> Contracts
    Contracts --> Core["Assimalign.Cohesion.Core"]
    Gateway["ApplicationModel.Gateway"] --> Definition
    HostingInProcess["ApplicationModel.Hosting.InProcess"] --> Hosting
    GatewayInProcess["ApplicationModel.Gateway.InProcess"] --> Gateway
    GatewayInProcess --> HostingInProcess
    AreaHosting["resources/*/….Hosting.ApplicationModel"] --> Hosting
    AreaHosting -.->|"COHRES003 ✗"| Gateway
```

Solid edges are the references the layering permits. The dotted edge is the one `COHRES003`
rejects: no shipped project under `resources/**` may resolve an
`Assimalign.Cohesion.ApplicationModel.Gateway*` assembly, because gateway orchestration belongs
outside resource-area packages. That prohibition has no opt-out and no exemption property.

| Project | Role |
| --- | --- |
| `Assimalign.Cohesion.ApplicationModel` | The shared contracts: the `IApplicationResource` base, resource and platform identity, the instance manifest, action invocation and results, state snapshots, the default JSON content serializer, and the two client seams a platform implements |
| `Assimalign.Cohesion.ApplicationModel.Definition` | The logical side: what one gateway runs, declared as a model: its resources with their dependencies, settings, and platform extensions, and the parent it joins |
| `Assimalign.Cohesion.ApplicationModel.Hosting` | The physical side: a running resource instance, its health contributors, its control plane, the host service that publishes and restores through a platform's client, and the options every area's resource opts into platforms with |
| `Assimalign.Cohesion.ApplicationModel.Hosting.InProcess` | The in-process platform's instance half: `UseInProcess()` on a resource's options; it publishes to its gateway's in-process endpoint and attaches the instance's control plane there for direct calls |
| `Assimalign.Cohesion.ApplicationModel.Gateway` | The gateway: deploys its model's resources through an infrastructure provider, keeps each resource's deployment across its instances, receives instance publications and child gateways' joins, runs operators' actions, and exposes infrastructure actions; `ApplicationBuilder` declares the model and builds the gateway |
| `Assimalign.Cohesion.ApplicationModel.Gateway.InProcess` | The in-process platform's gateway half: runs each resource's program on a thread of its own in the gateway's process, in an environment scope of its own, receives their publications over a channel, and calls their attached control planes directly |

## Further reading

- [docs/libraries/ApplicationModel/DESIGN.md](../../docs/libraries/ApplicationModel/DESIGN.md) — the area architecture record
- [docs/APPLICATION_MODEL_BUILD_AND_DEPLOY.md](../../docs/APPLICATION_MODEL_BUILD_AND_DEPLOY.md) — the build and deploy flow drawn: SDK generation, gateway selection, and the in-process, Local, and Kubernetes realizations side by side
- [docs/programs/REALIZATION_PLAN.md](../../docs/programs/REALIZATION_PLAN.md) — the platform-neutral realization plan contract
- [docs/RUNTIME_CONTRACT.md](../../docs/RUNTIME_CONTRACT.md) — the language-neutral `COHESION_*` runtime contract
- Per-project `docs/OVERVIEW.md` and `docs/DESIGN.md` beside each project's `src/`
