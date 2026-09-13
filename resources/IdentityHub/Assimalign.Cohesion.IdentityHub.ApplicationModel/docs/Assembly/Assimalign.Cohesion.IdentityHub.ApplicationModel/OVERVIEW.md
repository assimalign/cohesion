# Assimalign.Cohesion.IdentityHub.ApplicationModel

## Public surface

- `IdentityHubResource` — manifest-backed planned resource.
- `IdentityHubResourceOptions` — replica/storage option carrier; the planner currently
  permits one replica and supports `Storage.Size`.
- `IdentityHubResourceExtensions.AddIdentityHub(...)` — adds the typed resource to an
  application graph.
- `IdentityHubResourceControlPlane.Create()` — creates an isolated default control plane
  accepting `identityhub.add-audience` and `identityhub.add-client`.
- `IIdentityHubResourceDescriptor` retains typed resource and command/dependency surfaces.
- `IdentityHubResourceCommandExtensions` supplies AddAudience and AddClient declarations.

All types are platform-neutral and NativeAOT-compatible.
