# ApplicationModel Library Area

> **Being rebuilt (2026-10-27).** `Assimalign.Cohesion.ApplicationModel` and
> `Assimalign.Cohesion.ApplicationModel.Gateway` have been rebuilt on
> `Assimalign.Cohesion.Hosting.Resources`; see their
> [model](Assimalign.Cohesion.ApplicationModel/docs/DESIGN.md) and
> [gateway](Assimalign.Cohesion.ApplicationModel.Gateway/docs/DESIGN.md) designs.
> `Gateway.InProcess` and `Gateway.ControlPlane` are not rebuilt yet, and the rest of this page and
> the linked area records describe the retired design.

The L2 application-composition and orchestration plane: the declarative application model every
resource area's `<Area>.ApplicationModel` package builds on, plus the gateway family that
describes and realizes resource manifests without loading their runtimes.

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
references `Gateway`". The gateway packages layer onto the base model; nothing in the base model
references a gateway.

```mermaid
flowchart LR
    Model["Assimalign.Cohesion.ApplicationModel"] --> Resources["Assimalign.Cohesion.Hosting.Resources"]
    Model --> Core["Assimalign.Cohesion.Core"]
    Gateway["ApplicationModel.Gateway"] --> Model
    InProcess["ApplicationModel.Gateway.InProcess"] --> Gateway
    ControlPlane["ApplicationModel.Gateway.ControlPlane"] --> Model
    AreaModel["resources/*/…​.ApplicationModel"] --> Model
    AreaModel -.->|"COHRES003 ✗"| Gateway
```

Solid edges are the references the layering permits. The dotted edge is the one `COHRES003`
rejects: no shipped project under `resources/**` may resolve an
`Assimalign.Cohesion.ApplicationModel.Gateway*` assembly, because gateway orchestration belongs
outside resource-area packages. That prohibition has no opt-out and no exemption property.

| Project | Role |
| --- | --- |
| `Assimalign.Cohesion.ApplicationModel` | Application sets (what each gateway deploys), resource descriptors and their permission-carrying dependencies, the global manifest, and the gateway-side aggregate of a resource's instances and state |
| `Assimalign.Cohesion.ApplicationModel.Gateway` | The gateway: deploys its part of the hierarchy through an infrastructure provider, receives instance publications, and exposes infrastructure actions |
| `Assimalign.Cohesion.ApplicationModel.Gateway.InProcess` | The in-process Composite plan controller — compiles the supported in-process subset of a Composite plan |
| `Assimalign.Cohesion.ApplicationModel.Gateway.ControlPlane` | The gateway-side control-plane contract that a resource's default control plane is reached through |

## Further reading

- [docs/libraries/ApplicationModel/DESIGN.md](../../docs/libraries/ApplicationModel/DESIGN.md) — the area architecture record
- [docs/APPLICATION_MODEL_BUILD_AND_DEPLOY.md](../../docs/APPLICATION_MODEL_BUILD_AND_DEPLOY.md) — the build and deploy flow drawn: SDK generation, gateway selection, and the in-process, Local, and Kubernetes realizations side by side
- [docs/programs/REALIZATION_PLAN.md](../../docs/programs/REALIZATION_PLAN.md) — the platform-neutral realization plan contract
- [docs/RUNTIME_CONTRACT.md](../../docs/RUNTIME_CONTRACT.md) — the language-neutral `COHESION_*` runtime contract
- Per-project `docs/OVERVIEW.md` and `docs/DESIGN.md` beside each project's `src/`
