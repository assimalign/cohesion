# Vendored official OpenAPI meta-schemas

The `OpenApiSchemaConformanceRule` evaluates a serialized document against the official OpenAPI
meta-schema for its version. These files are the official schemas, embedded as assembly resources so
the conformance stage works offline and deterministically.

| File | Source `$id` | Dialect |
|---|---|---|
| `oas-3.0-schema.json` | `https://spec.openapis.org/oas/3.0/schema/2024-10-18` | JSON Schema draft-04 |
| `oas-3.1-schema.json` | `https://spec.openapis.org/oas/3.1/schema/2025-11-23` | JSON Schema draft 2020-12 |
| `oas-3.2-schema.json` | `https://spec.openapis.org/oas/3.2/schema/2025-11-23` | JSON Schema draft 2020-12 |

The self-contained `schema` iterations are vendored (not the thin `schema-base` wrappers, which
`$ref` remote dialect documents). The 3.1/3.2 schemas leave the Schema Object itself validated by the
JSON Schema dialect they embed via `$dynamicAnchor: meta`.

## License

The schemas are published by the OpenAPI Initiative in `github.com/OAI/OpenAPI-Specification` under the
**Apache License 2.0**. They are redistributed here unmodified under that license.

## Authority

Per the OpenAPI Initiative publications, the schema files are **informational**: where a schema and the
specification text disagree, the specification text is authoritative. The conformance stage therefore
reports divergences as `Warning`-severity diagnostics (`OPENAPI4001`), never as errors.

## Updating

Replace a file with a newer official iteration and update the `$id` row above. The evaluator supports
the keyword set these schemas use; a new iteration that introduces an unhandled constraint keyword
would silently pass it (a vacuous pass), so re-run the official example corpus check after any update
to confirm no regressions.
