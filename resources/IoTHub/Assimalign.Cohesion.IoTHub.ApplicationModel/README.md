# Assimalign.Cohesion.IoTHub.ApplicationModel

This package describes resource manifests, dependencies and deployment plans. Its only direct Cohesion dependency is ApplicationModel, whose contracts depend only on Core. Sdk.ApplicationModel generates control-plane registration in the enabled resource executable; Hosting.Resources and the resource host own local state handlers. The gateway owns infrastructure observation and management reconciliation.

- [Overview](docs/OVERVIEW.md)
- [Design](docs/DESIGN.md)
