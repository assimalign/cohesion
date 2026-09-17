# Assimalign.Cohesion.Database.Documents.Language — Design

This package owns the document model's Object Query Language (OQL) vocabulary. Its public types
live in `Assimalign.Cohesion.Database.Documents.Language`, matching the model assembly rather than
nesting under the shared `Assimalign.Cohesion.Database.Language` namespace.

## Language profile

`OqlLanguageProfile.Instance` is the single declaration of the OQL grammar surface. It supplies
the existing ODMG-based keyword and builtin-function tables together with model-owned clause
capabilities. Consumers project that profile to `TokenLexerOptions`; they do not maintain a second
lexer configuration.

`OqlClauses` gives future parser code stable names for the OQL capabilities it gates, including the
core `SELECT` / `FROM` / `WHERE` query shape and OQL-specific collection operations such as
`FLATTEN`. Clause matching is case-insensitive, as is the lexical vocabulary.

## Current boundary

This package does not contain an OQL parser yet. Feature B3 will add the grammar, AST, diagnostics,
and conformance corpus against this profile. The profile and constants landed first so that work
starts from the same per-model capability contract used by the other query languages.

## AOT posture

The profile is static data projected onto the shared lexer. The package uses no reflection,
runtime discovery, or `Microsoft.Extensions.*` dependencies and remains NativeAOT compatible.
