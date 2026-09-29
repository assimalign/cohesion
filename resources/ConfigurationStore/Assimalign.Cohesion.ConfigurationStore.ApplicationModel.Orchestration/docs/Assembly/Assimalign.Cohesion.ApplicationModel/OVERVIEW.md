# Assimalign.Cohesion.ConfigurationStore.ApplicationModel.Orchestration

Namespace: `Assimalign.Cohesion.ApplicationModel`

Assembly: `Assimalign.Cohesion.ConfigurationStore.ApplicationModel.Orchestration`

## Purpose

The assembly is the opt-in ConfigurationStore provider for a gateway: it resolves
`<store>:<namespace>` Configuration mount sources by reading the namespace from a ConfigurationStore
resource of the application. It declares its types in the shared `Assimalign.Cohesion.ApplicationModel`
namespace, so the gateway's `Program.cs` composes it with the same `using` as the area verbs; the
types are area-prefixed to stay unambiguous there.

## Public surface

- [`ConfigurationStoreSourceProvider`](ConfigurationStoreSourceProvider/OVERVIEW.md) — the
  `IResourceSourceProvider` that reads one namespace per Configuration mount.
- [`ConfigurationStoreOrchestrationExtensions`](ConfigurationStoreOrchestrationExtensions/OVERVIEW.md)
  — the `UseConfigurationStore(store)` registration verbs: by descriptor on `IApplicationBuilder`, and
  by name on `IApplicationProviderBuilder` for application-set members.

## Links

- [Project overview](../../OVERVIEW.md)
- [Project design](../../DESIGN.md)
