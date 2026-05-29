# OpenApi

The OpenApi area provides a code-first, version-aware foundation for producing and validating OpenAPI
descriptions across the officially published **3.0.4**, **3.1.2**, and **3.2.0** lines. It is an L1
foundation library family: it has no dependency on Web, ApiManager, or any service runtime, so L2/L3
service layers can compose it through contracts and adapters rather than inheriting hosting concerns.

## Layering

- **L1 (this area):** the document model, serialization, and validation — pure description machinery.
- **L2/L3 (future):** Web endpoint metadata and ApiManager contract workflows feed this foundation
  through integration contracts (Wave 3), keeping service runtime concerns out of the root model.

## Project family

Dependency direction is one-way: every package depends on the root model; no package depends on a
sibling except through the model.

| Package | Responsibility | Depends on | Status |
|---|---|---|---|
| [`Assimalign.Cohesion.OpenApi`](./Assimalign.Cohesion.OpenApi/) | Canonical version-aware model, capability matrix, `OpenApiNode` value tree | — | Implemented |
| [`Assimalign.Cohesion.OpenApi.Serialization`](./Assimalign.Cohesion.OpenApi.Serialization/) | Model ↔ node-tree mapping; JSON read/write (YAML fast-follow) | model | Implemented (JSON) |
| [`Assimalign.Cohesion.OpenApi.Validation`](./Assimalign.Cohesion.OpenApi.Validation/) | Diagnostics model; structural, semantic, and version-placement rules | model | Implemented |
| `Assimalign.Cohesion.OpenApi.Fluent` | Fluent authoring builders | model | Planned (Wave 2) |
| `Assimalign.Cohesion.OpenApi.Attributes` | Attribute metadata model | model | Planned (Wave 2) |
| `Assimalign.Cohesion.OpenApi.SourceGeneration` | AOT-safe attribute discovery → descriptors | attributes, model | Planned (Wave 2) |
| `Assimalign.Cohesion.OpenApi.Generation` | Generation orchestration | model, serialization, attributes | Planned (Wave 2) |

These boundaries are advisory architecture guidance. They exist to preserve, respectively: additive
format support (YAML and beyond) without touching the model; source-generator-first, reflection-free
attribute discovery for NativeAOT; a pluggable official-schema validation stage; and a clean
Web/ApiManager integration seam.

## Standards

Supports OpenAPI 3.0.4, 3.1.2, and 3.2.0 with version-aware authoring, serialization, and validation.
Where schema files and specification text disagree, the specification text is authoritative per the
OpenAPI Initiative publications.

## Status and roadmap

Wave 1 (model + JSON serialization + validation) is implemented. The immediate fast-follow is **YAML
serialization** (the node-tree seam is already in place). Subsequent work covers fluent authoring,
the attribute model and AOT source generator, version transforms, advanced authoring surfaces, an
official example/upgrade compliance corpus, and Web/ApiManager integration contracts.

See each package's `docs/OVERVIEW.md` and `docs/DESIGN.md` for detail.
