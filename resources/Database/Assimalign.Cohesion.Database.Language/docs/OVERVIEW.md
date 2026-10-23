# Assimalign.Cohesion.Database.Language

Shared lexical and parsing infrastructure for Cohesion's SQL, OQL and GQL model languages.
The assembly references Database.Types for diagnostics and contains no model grammar or engine.

A model declares a `QueryLanguageProfile` with its keywords, functions, supported clauses and
case policy, then derives from `QueryParser` to build its statement and expression nodes.
`TokenLexer` borrows source spans and recognizes the shared token and line-terminator rules.
`Location` retains positions supplied by the model parser; it does not normalize them.

Configure `QueryParserOptions.Analyzers` before parsing to run custom `QueryAnalyzer` instances
over the parsed statement. Analyzers add diagnostics through `QueryAnalyzerContext.Statement`;
the parser constructs this sealed context internally.
Parsing waits for all analyzers within the shared `AnalyzerTimeout` (five seconds by default);
analyzers must honor cancellation and avoid depending on the parsing caller's synchronization
context. Analyzer failures propagate to the caller. Statements reject null diagnostics and
expose diagnostics ordered by severity and then source end position.

See [DESIGN.md](DESIGN.md) for language boundaries, lexical rules and diagnostic conventions.

## Error and construction conventions (#1470)

Exception messages and format templates are generated from `src/Properties/ErrorMessages.resx`;
format arguments use the current culture. Cancellation and abort templates used during normal
cleanup are cached before reuse. Error identities remain separate from message text where a
structured diagnostic carrier exists. Standard .NET guards retain their argument names and exception types.


## Debugger inspection (#1470)

Eligible concrete source types use private nested debugger views of reviewed public/protected
state; existing inherited display/proxy pairs are reused. See the shared
[debugger convention](../../Assimalign.Cohesion.Database/docs/CONVENTIONS.md#debugger-inspection).
Borrowed token spans and lazy statement diagnostics are omitted. The lexer view retains no ref-like instance; profile views expose already stored vocabulary metadata without enumeration.
Runtime APIs, methods, allocation budgets, persistent formats and wire behavior are unchanged.
