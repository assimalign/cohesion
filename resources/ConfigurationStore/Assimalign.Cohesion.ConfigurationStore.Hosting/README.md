# Assimalign.Cohesion.ConfigurationStore.Hosting

The ConfigurationStore area's runtime composition root. `ConfigurationStoreApplication.CreateBuilder(args)`
captures code-first namespace declarations and builds either a plain host or, when resource opt-in is
enabled, the area's default control-plane endpoint.

The enabled host stores each namespace as plain JSON beneath the durable `data` volume and serves
authenticated list, read, set, and remove operations under `/cohesion/v1`. ES256 bootstrap JWTs are
verified against the application's durable trusted-issuer set; the ambient application key seeds the
set on first start and reconciles key rotation. Declarations seed only absent namespace documents, and
mutations become visible only after their atomic durable write succeeds.

The HTTP implementation is internal and composed from Cohesion Hosting, Hosting.Resources,
Hosting.Health, IdentityModel, and the private Web transport. Public construction remains limited to
`ConfigurationStoreApplication`; the resource still behaves as a plain application when opt-in is
disabled.

- [Overview](docs/OVERVIEW.md)
- [Design](docs/DESIGN.md)
