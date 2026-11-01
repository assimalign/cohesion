# Assimalign.Cohesion.ConfigurationStore.Hosting

The ConfigurationStore area's runtime composition root. `ConfigurationStoreApplication.CreateBuilder(args)`
captures code-first namespace declarations and builds a plain host.

The host stores each namespace as plain JSON beneath the durable `data` directory and serves
list and read operations under `/cohesion/v1`. The area's resource integration is
`Assimalign.Cohesion.ConfigurationStore.Hosting.ApplicationModel` (`builder.Services.AddResource(...)`), which references this module; this module references no application-model library. Declarations seed only absent
namespace documents.

The HTTP implementation is internal and composed from Cohesion Hosting and the private Web
transport. Public construction remains limited to `ConfigurationStoreApplication`.

- [Overview](docs/OVERVIEW.md)
- [Design](docs/DESIGN.md)
