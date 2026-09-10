# Assimalign.Cohesion.IdentityHub.ApplicationModel

The IdentityHub area's declarative, AOT-compatible orchestration package. It wraps
an enabled IdentityHub executable's build-produced `ResourceManifest`, produces a
platform-neutral `ResourcePlan`, and supplies the area's default control-plane factory.

The planner preserves the service's initial stateful contract: a required `https`
endpoint, one persistent `data` volume, exactly one stable replica, a sized per-replica
claim, and a headless governing service. Additional endpoints and non-persistent
Configuration or Secret mounts remain generic manifest traits, so Hosting can consume a
gateway-materialized `tls` mount. `IdentityHubResourceOptions.Storage.Size` is the only
area-specific planning override in this item.

`IdentityHubResourceControlPlane.Create()` returns a fresh isolated control plane.
Its accepted command-kind set is empty; `AddAudience` and `AddClient` belong to
developer-experience item 31c.

## Documentation

- [Overview](docs/OVERVIEW.md)
- [Design](docs/DESIGN.md)
- [Public API](docs/Assembly/Assimalign.Cohesion.IdentityHub.ApplicationModel/OVERVIEW.md)
