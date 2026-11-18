# Fabric (`fabric/**`)

`fabric/` is the application model's root: the resource envelope, `$ref`, the fault, provider,
and platform contracts (`fabric/Fabric/`), the fabric areas (`fabric/Workloads/`,
`fabric/Networking/`, `fabric/Storage/`), and the in-process platform (`fabric/InProcess/`). The
design is the v2 "application model as resource providers" document (owner approved the layout
2026-11-18; the design's §15 decisions are still open). Read `fabric/README.md` and the project's
`docs/DESIGN.md` before changing a fabric project.

## Layout

```
fabric/
├── Directory.Build.props / .targets      same defaults as libraries/ (AOT, net10.0)
├── Fabric/Assimalign.Cohesion.Fabric/     the root
├── Workloads/Assimalign.Cohesion.Workloads/
├── Networking/Assimalign.Cohesion.Networking/
├── Storage/Assimalign.Cohesion.Storage/
└── InProcess/Assimalign.Cohesion.Fabric.InProcess/
```

Each project has `{src,tests,docs}` like a library, resolves by name through
`CohesionProjectReference` and `CohesionSharedSource` (both index `fabric\**`), and is built by
its own `.github/workflows/fabric-<area>.yml` and listed in `scripts/modules/CohesionPackaging.psm1`.

## Rules

| Rule | Says | Enforced by |
| --- | --- | --- |
| **F1** | `fabric/` never references `resources/`; it may reference `libraries/` | **COHLIB001**, whose scope includes `fabric/**` |
| **F2** | `fabric/` holds no platform client and no out-of-process driver; only the in-process platform | Review |
| **F3** | Inside `resources/`, only `Assimalign.Cohesion.ResourceManager.Hosting` and an area's `Assimalign.Cohesion.<Area>.ResourceProvider` may resolve a `fabric/**` assembly | **COHRES005** |
| **F4** | Fabric areas do not reference each other; they meet through `$ref` | **COHFAB001** |
| **F5** | A platform contract package never references a platform client | `cohesion-platforms` (not yet written) |
| — | An `<Area>.ResourceProvider` resolves no `Hosting*` assembly and not its own area root | **COHRES006** (area root); COHRES001 and COHRES004 already refuse hosting assemblies to it |

All are in `build/Targets/Build.Rules.targets` and check two layers: the project-reference graph
by the referenced project's path, then the resolved assembly closure by name, which catches a
package-delivered or `HintPath` copy. Harness folders (`tests`, `examples`, `samples`, `fixtures`)
are exempt. None has an opt-out property.

- **COHFAB001** treats every folder under `fabric/` as an area except `Fabric/` (the root, which
  references no area) and `InProcess/` (a platform, which implements all of them). A new platform
  folder under `fabric/` needs the same carve-out in the rule.
- **COHRES005** also exempts the ResourceManager area's own framework producers by exact identity,
  because its runtime framework carries `ResourceManager.Hosting` and with it the fabric closure.

## Code rules specific to fabric

- **Unknown fields are fatal.** Every JSON contract is a source-generated `JsonSerializerContext`
  with `UnmappedMemberHandling = Disallow`, camelCase names, and string enums. A fabric area ships
  its own context for its spec types.
- **A `$ref` is structural.** Spec values that may point at another resource are `FabricRef` or
  `FabricValue<T>`, never a string the ResourceManager interpolates. A literal platform object
  name is refused where a reference belongs.
- **Specs grow; contracts stay small.** Spec types (`WorkloadSpec`, `ExposureSpec`, `VolumeSpec`)
  are init-property records so portable fields can be added. Driver and provider contracts
  (`IResourceProvider`, `IWorkloadDriver<T>`, `IFabricPlatform`) are a cross-repository API that
  `cohesion-platforms` pins: change them deliberately.
- **Platforms add reasons, never fault codes.** `FaultCode` is closed.
- **Identity is the uid.** Store an edge by `ResourceUid`, never by `ResourceId`.
- **Open design decisions stay open.** Do not add the system driver (decision 2), built-in roles
  (decision 4), or tier entries (decision 9) until the owner decides them.
