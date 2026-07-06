# OpenApi compliance coverage matrix

This matrix records what the `Assimalign.Cohesion.OpenApi` family is measured against — the vendored
official example corpus (`../tests/Shared/Corpus/`, CC-BY-4.0, see its `NOTICE.md`) plus the
version-upgrade fixtures — and what is deliberately out of scope. It is the coverage contract downstream
service integrations can rely on when choosing features or version targets.

The suite is hosted in this project's [`tests/`](../tests/) (`Compliance/` test classes over the shared
`Shared/Corpus/` fixtures); other test projects can run their own corpus tests by importing
`tests/Shared/OpenApiCorpus.props`.

## What runs against every corpus example

Each vendored example (JSON and YAML) is exercised by `OpenApiCorpusComplianceTests`:

| Check | JSON | YAML |
|---|:---:|:---:|
| Parses without error | ✅ | ✅ |
| Deterministic round-trip (parse → emit → parse → emit is stable) | ✅ | ✅ |
| Validates with no `Error` diagnostics | ✅ | (via JSON) |
| JSON and YAML forms produce the same model | ✅ (equivalent pairs only — see gaps) | ✅ |

## Corpus by version and surface

| Version | Example | Documentation | Callbacks | Webhooks | Links | Security | 3.2 features |
|---|---|:---:|:---:|:---:|:---:|:---:|:---:|
| 3.0.4 | `petstore` | ✅ | – | – | – | – | – |
| 3.0.4 | `api-with-examples` | ✅ | – | – | – | – | – |
| 3.0.4 | `callback-example` | ✅ | ✅ | – | – | – | – |
| 3.0.4 | `link-example` | ✅ | – | – | ✅ | – | – |
| 3.1.2 | `webhook-example` | ✅ | – | ✅ | – | – | – |
| 3.1.2 | `tictactoe` | ✅ | ✅ | ✅ | ✅ | ✅ | – |
| 3.1.2 | `non-oauth-scopes` | ✅ | – | – | – | ✅ | – |
| 3.2.0 | `3.2-query-example` | ✅ | – | – | – | – | ✅ (QUERY, querystring) |
| 3.2.0 | `3.2-tags-example` | ✅ | – | – | – | – | ✅ (tag hierarchy) |

## Version-upgrade fixtures

`OpenApiUpgradeComplianceTests` turns the official upgrade guidance into executable checks:

| Transform | Examples | Assertion |
|---|---|---|
| 3.0 → 3.1 | petstore, callback, link, api-with-examples | targets 3.1, validates clean, emits `openapi: 3.1.2` |
| 3.0 → 3.2 | petstore, callback | targets 3.2, validates clean |
| 3.1 → 3.2 | webhook, tictactoe, non-oauth-scopes | targets 3.2, validates clean |
| 3.1 → 3.0 (downgrade) | webhook | reports `webhooks` as an unsupported construct |
| upgrade then identity | petstore | second same-version transform is warning-free and byte-stable |

## Known gaps and deliberate exclusions

- **`tictactoe` and `3.2-query-example` JSON/YAML are not the same document upstream.** The official
  corpus does not keep these two pairs in sync — their YAML forms carry advanced surfaces (webhooks,
  callbacks, links) their JSON forms omit. They are excluded from the *format-equivalence* theory only;
  both forms still parse, round-trip, and validate. This is an upstream data discrepancy, not a library
  limitation.
- **Corpus subset, not the full repository.** A curated subset is vendored to cover every major surface
  across the three lines without importing the entire (large, frequently-churning) upstream corpus.
  `petstore-expanded` and `uspto` are intentionally not vendored; their surfaces are already covered by
  `petstore` and `tictactoe`.
- **Nested-schema version-placement reporting** during a downgrade is coarse — see the Versioning
  library's `DESIGN.md` non-goals. Document-, operation-, and component-level drops are reported;
  keywords buried inside inline schemas are gated on serialization without an individual diagnostic.
- **Official JSON Schema meta-schema validation** is an exposed extension point, not a bundled engine —
  see the Validation library's `DESIGN.md`. The corpus is validated against the structural, semantic,
  and version-placement rules, which the official examples pass.
