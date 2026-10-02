# Assimalign.Cohesion.OpenApi.Attributes

An attribute model for describing OpenApi metadata in application code, the flat intermediate metadata
those attributes map to, and a mapper that applies the mapping rules with diagnostics. The metadata is
the contract the AOT-safe source generator emits and the generation pipeline consumes. This package
carries that generator under `analyzers/dotnet/cs/`, so referencing it is enough to get an internal,
compile-time `OpenApiMetadataRegistry` that combines your own annotated code with every annotated
assembly you reference. The two public types that carry metadata between assemblies,
`IOpenApiMetadataProvider` and `[assembly: OpenApiMetadataProvider]`, live here too.

- [docs/OVERVIEW.md](./docs/OVERVIEW.md) — purpose, scope, and usage
- [docs/AUTHORING.md](./docs/AUTHORING.md) — how to annotate code and the mapping rules
- [docs/DESIGN.md](./docs/DESIGN.md) — the three-layer design and AOT posture
