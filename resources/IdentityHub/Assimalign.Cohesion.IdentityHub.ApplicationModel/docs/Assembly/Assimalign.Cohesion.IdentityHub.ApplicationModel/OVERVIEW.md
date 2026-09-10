# Assimalign.Cohesion.IdentityHub.ApplicationModel

## Public surface

- `IdentityHubResource` — manifest-backed planned resource.
- `IdentityHubResourceOptions` — replica/storage option carrier; the planner currently
  permits one replica and supports `Storage.Size`.
- `IdentityHubResourceExtensions.AddIdentityHub(...)` — adds the typed resource to an
  application graph.
- `IdentityHubResourceControlPlane.Create()` — creates an isolated default control plane
  with no accepted command kinds until item 31c.

All types are platform-neutral and NativeAOT-compatible.
