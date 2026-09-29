# Assimalign.Cohesion.Database.Language — Design

The shared query-language substrate for database models. It owns model-neutral lexical tokens,
syntax-tree primitives, diagnostics, and parser orchestration; SQL, OQL, GQL, and future model
languages own their vocabulary and grammar.

## Design intent

Each model declares one `QueryLanguageProfile`. The profile is the complete boundary between the
shared parser machinery and a model language: it supplies the lexer's keyword and function tables,
names the language used in diagnostics, and lists the clauses that model's parser accepts. A parser
therefore cannot silently inherit a flat, database-wide grammar.

## Why-this-not-that decisions

- **Language-specific clause names, not a shared enum.** SQL `JOIN`, OQL `FLATTEN`, and GQL `MATCH`
  are different vocabularies. Model packages publish constants for their own clause names, while
  the shared profile compares strings using the same case policy as its lexical tables.
- **One parser declaration, not parallel options.** `QueryParser.Profile` replaces the former
  lexer-options member. Lexer construction projects that profile through `ToLexerOptions()`, so
  lexical behavior and parser capabilities cannot be configured independently by accident.
- **Defensive profile snapshots.** Constructor arrays are copied and clauses are exposed through a
  read-only view. Registration code can safely reuse its source arrays without mutating a running
  parser's grammar surface.
- **One unsupported-clause diagnostic.** `QueryDiagnostics.UnsupportedClause` emits stable code
  `COHDBL001` and a shared message naming the rejected clause and model language. Conformance
  corpora can distinguish an intentionally unsupported capability from malformed syntax.
- **Parser-level gating.** Model parsers call `RequireClause` at the recognized clause boundary.
  The helper records the shared diagnostic and returns whether parsing may continue; the shared
  package does not attempt to understand any model's grammar.

## Non-goals

- No shared clause enum or universal grammar.
- No parser implementation for a particular database model.
- No reflection-based discovery or runtime registration of language profiles.

## AOT posture

Profiles use copied arrays, a comparer-backed set, and direct construction. Lexer projection and
diagnostic creation require no reflection, dynamic code generation, or dependency-injection stack.
