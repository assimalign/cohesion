# Vendored official OpenAPI example corpus

The files under `v3.0/`, `v3.1/`, and `v3.2/` in this directory are a curated subset of the official
OpenAPI Initiative example descriptions, vendored verbatim as a compliance corpus.

- **Source:** <https://github.com/OAI/learn.openapis.org> (`examples/` directory), published at
  <https://learn.openapis.org/examples/>.
- **License:** Creative Commons Attribution 4.0 International (CC-BY-4.0). See
  <https://creativecommons.org/licenses/by/4.0/>.
- **Attribution:** © The OpenAPI Initiative and the `learn.openapis.org` contributors. The files are
  used here unmodified, solely as conformance fixtures for the `Assimalign.Cohesion.OpenApi` library.

## Vendored files

| Version | Example | Surfaces exercised |
|---|---|---|
| 3.0 | `petstore` | baseline documentation, paths, parameters, responses |
| 3.0 | `petstore-expanded` (not vendored; see coverage matrix) | — |
| 3.0 | `api-with-examples` | media-type examples, multiple response codes |
| 3.0 | `callback-example` | callbacks, request bodies, runtime expressions |
| 3.0 | `link-example` | links, operationRef/operationId |
| 3.1 | `webhook-example` | top-level webhooks |
| 3.1 | `tictactoe` | complex schemas, components, path parameters |
| 3.1 | `non-oauth-scopes` | security schemes, scopes |
| 3.2 | `3.2-query-example` | the QUERY operation, querystring content |
| 3.2 | `3.2-tags-example` | tag hierarchy (`parent`/`kind`/`summary`) |

Each example is vendored in both its JSON and YAML forms so the corpus exercises both formatters.
