# Assimalign.Cohesion.Database.Graph.Language — Design

This package owns the graph model's Graph Query Language (GQL) vocabulary. Its public types live in
`Assimalign.Cohesion.Database.Graph.Language`, matching the model assembly rather than nesting
under the shared `Assimalign.Cohesion.Database.Language` namespace.

## Language profile

`GqlLanguageProfile.Instance` is the single declaration of the GQL grammar surface. It combines
the existing keyword and builtin-function tables with the graph model's clause capabilities, then
projects that data to the shared lexer through `QueryLanguageProfile`.

`GqlClauses` provides stable names for graph-specific capabilities such as `MATCH`, staged
projection, graph mutation, path search, and procedure calls. Clause, keyword, and function
matching are case-insensitive.

## Standards alignment and current boundary

The vocabulary remains aligned with ISO/IEC 39075, the graph query standard selected for the
database program. This run deliberately adds no parser. Feature B4 will build the grammar, AST,
diagnostics, and conformance corpus against the profile without changing the selected standard.

## AOT posture

The profile is static data projected onto the shared lexer. The package uses no reflection,
runtime discovery, or `Microsoft.Extensions.*` dependencies and remains NativeAOT compatible.
