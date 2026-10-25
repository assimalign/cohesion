# Assimalign.Cohesion.EmailHub.ApplicationModel

This package describes resource manifests, dependencies and deployment plans. Its only direct Cohesion dependency is ApplicationModel, whose contracts depend only on Core. Sdk.ApplicationModel generates control-plane registration in the enabled resource executable; Hosting.Resources and the resource host own the runtime surface and apply the deployment record. The gateway observes infrastructure and delivers the record.

- [Overview](docs/OVERVIEW.md)
- [Design](docs/DESIGN.md)
