# Assimalign.Cohesion.Database.Language

Shared lexical and parsing infrastructure for Cohesion's SQL, OQL and GQL model languages.
The assembly references Database.Types for diagnostics and contains no model grammar or engine.

A model declares a `QueryLanguageProfile` with its keywords, functions, supported clauses and
case policy, then derives from `QueryParser` to build its statement and expression nodes.
`TokenLexer` borrows source spans and recognizes the shared token and line-terminator rules.
`Location` retains positions supplied by the model parser; it does not normalize them.

Configure `QueryParserOptions.Analyzers` before parsing to run custom `QueryAnalyzer` instances
over the parsed statement. Analyzers add diagnostics through `QueryAnalyzerContext.Statement`.
Parsing waits for all analyzers within the shared `AnalyzerTimeout` (five seconds by default);
analyzers must honor cancellation and avoid depending on the parsing caller's synchronization
context. Analyzer failures propagate to the caller. Statements reject null diagnostics and
expose diagnostics ordered by severity and then source end position.

See [DESIGN.md](DESIGN.md) for language boundaries, lexical rules and diagnostic conventions.
