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

## Keyword disposition and stray characters (#1101)

Every word a language recognizes has one of two dispositions: it parses inside a supported
clause, or it is rejected with exactly one `COHDBL001` at its own span whose message names the
construct. It is never bound as a name. A character no language uses is rejected with that
language's syntax code. The rules, shared by SQL, OQL and GQL:

- **One recognized-unsupported table per language.** Each language package keeps one static
  table, `SqlUnsupportedVocabulary`, `OqlUnsupportedVocabulary` or `GqlUnsupportedVocabulary`.
  Each entry pairs a spelling with the construct its diagnostic names and the position where
  the parser treats it as that construct. The tables are internal arrays in each package's
  `Internal` namespace (`src/Internal/`). Each language's test project reaches them through
  `InternalsVisibleTo`, and nothing else does. Later items extend the tables: shared-diagnostics
  adds function names, and oql-sqlpp-basis adds the ODMG words.
- **Recognized-but-unsupported words stay positional.** A word such as OQL `LIMIT` or `MERGE`, or
  GQL `TRAIL` or `NODETACH`, is not added to the profile's keyword list. The lexer keeps it an
  identifier, so a field or variable of that name still parses. The table's position rule decides
  where the word starts a construct instead. Examples: the start of the statement, the token
  after an operand, or the token before a path pattern.
- **`COHDBL001` is for recognized constructs.** That covers a clause, a statement, a multi-word
  prefix such as GQL `ALL SHORTEST`, a quantified predicate, a label-expression operator, or an
  operator such as SQL `~`. A statement whose first word is unsupported is one construct; the
  scan reports that word and nothing after it. A construct owns its modifiers and its operand:
  GQL `ALL TRAIL PATHS` and OQL `UNION ALL` are one construct each, and SQL skips the text of
  `= ANY (SELECT ...)`, `LATERAL (...)` or `WITHIN GROUP (...)` once it is reported, so that text
  adds no second diagnostic.
- **Each table has a corpus.** `SqlKeywordDispositionTests`, `OqlKeywordDispositionTests` and
  `GqlKeywordDispositionTests` enumerate the profile's keywords plus the table. They fail unless
  every word has a positive case inside a supported clause, or a case with exactly one
  `COHDBL001` naming its construct. For a table entry, that diagnostic must also name the
  construct the table records, so the table's construct cannot drift from the parser's message.
  A self-test proves that a word added without a case fails. So an item that adds a keyword or a
  table entry adds its corpus case in the same change.
- **Pins, and who flips them.** These stay `COHDBL001`, with no T1 item lifting them: GQL path
  modes, GQL shortest-path prefixes, GQL `MERGE`, GQL `NODETACH DELETE`, and OQL `MERGE`. #1101
  owns the OQL `MERGE` pin, so oql-upsert does not re-pin it. Each of these items depends on #1101
  and flips its own pins, corpus cases included, in its own change: oql-limit-offset (OQL `LIMIT`
  and `OFFSET`), oql-upsert (`UPSERT`), oql-arrays (`UNNEST` and `EVERY ... SATISFIES`), and
  gql-label-direction (label-expression operators and `IS`).
- **A stray character is `TokenType.Unrecognized`.** The lexer reports a character outside every
  language as `Unrecognized`, never as `Identifier`. Examples are `?`, `#`, `^`, `§`, a
  backtick, or a zero-width space. A surrogate pair is one token. Only ASCII digits make a numeric
  literal, so a Unicode decimal digit outside a name, such as Arabic-Indic `٣` or fullwidth `１`,
  is `Unrecognized` too; `char.IsDigit` used to lex it as an integer that the engines could not
  convert. Inside a name such a digit stays part of the identifier. `Unrecognized` is appended
  after `Eof`, so every existing member keeps its value. Each parser reports the character with its own
  syntax code at the character's span: `SQL0003`, `OQL0002` or `GQL0002`. The message is
  `Unexpected character '<c>'`, or `U+<hex>` for an invisible or supplementary character. The
  statement then binds nothing. OQL and GQL stop before syntax parsing. SQL parses on so that it
  can still report its other errors, then keeps only the command type; a stray character before
  the statement, such as an unstripped byte order mark, does not become its command, so it is
  the only error, as in OQL and GQL. `?` is a stray character
  like any other here; the message that names the supported parameter forms is
  shared-parameters'.
- **`--` stays a line comment in every language.** It is SQL's comment and ISO/IEC 39075's
  `<simple comment>`, so there is no per-language lexer switch. A GQL abbreviated edge written
  with `--` directly after `)` or `]` reads as a comment; the coded GQL diagnostic for that case
  belongs to gql-label-direction.

## Non-goals

- No shared clause enum or universal grammar.
- No parser implementation for a particular database model.
- No reflection-based discovery or runtime registration of language profiles.

## AOT posture

Profiles use copied arrays, a comparer-backed set, and direct construction. Lexer projection and
diagnostic creation require no reflection, dynamic code generation, or dependency-injection stack.
The recognized-unsupported tables are static arrays scanned by ordinary loops.
