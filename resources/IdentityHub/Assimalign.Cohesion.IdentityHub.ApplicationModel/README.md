# Assimalign.Cohesion.IdentityHub.ApplicationModel

This package describes resource manifests, dependencies and deployment plans. Its only direct Cohesion dependency is ApplicationModel, whose contracts depend only on Core. Sdk.ApplicationModel generates control-plane registration in the enabled resource executable; Hosting.Resources and the resource host own local state handlers. The gateway owns infrastructure observation and management reconciliation.

The planner preserves the service's initial stateful contract: a required `https`
endpoint, one persistent `data` volume, exactly one stable replica, a sized per-replica
claim, and a headless governing service. Additional endpoints and non-persistent
Configuration or Secret mounts remain generic manifest traits, so Hosting can consume a
gateway-materialized `tls` mount. `IdentityHubResourceOptions.Storage.Size` is the only
area-specific planning override in this item.

## Documentation

- [Overview](docs/OVERVIEW.md)
- [Design](docs/DESIGN.md)
- [API reference (cohesion-docs)](https://github.com/assimalign/cohesion-docs)
