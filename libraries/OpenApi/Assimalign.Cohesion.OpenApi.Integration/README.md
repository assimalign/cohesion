# Assimalign.Cohesion.OpenApi.Integration

Integration contracts that let the Web layer and ApiManager feed and consume OpenApi descriptions
without leaking service-runtime concerns into the model. Web implements `IOpenApiEndpointSource`; a
description provider composes sources into a version-targeted document; ApiManager imports and exports
through thin seams over serialization and the transform pipeline.

- [docs/OVERVIEW.md](./docs/OVERVIEW.md) — purpose, scope, and usage
- [docs/DESIGN.md](./docs/DESIGN.md) — the boundary, dependency direction, and AOT posture
