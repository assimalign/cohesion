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
  belongs to gql-label-direction. Where the comment ends is the next section's rule.

## Line terminators and line comments (#1150)

One set of line terminators serves every language: line feed (LF, U+000A), carriage return
(CR, U+000D), next line (NEL, U+0085), line separator (LS, U+2028) and paragraph separator
(PS, U+2029). `TokenLexer.IsLineTerminator(char)` is that set. The lexer and
`TokenLexer.CountLineBreaks` read it from the same static field, and code outside the lexer that
needs the same boundary calls `IsLineTerminator` instead of keeping its own list.

- **A `--` comment ends before the first terminator.** The terminator is not part of the
  `Comment` token; it is skipped as whitespace, so CR LF needs no special case and the next line
  lexes as code. A comment with no terminator after it runs to the end of the input, as before.
  The comment used to end only at LF, so the text after a lone CR, NEL, LS or PS was comment
  text: `DELETE FROM t -- note<CR>WHERE id = 1` parsed as an unfiltered `DELETE` and removed
  every row, and #1068's trailing-token rule could not see it, because the swallowed text was
  part of the comment token rather than left over after the statement. PostgreSQL ends a `--`
  comment at CR or LF, and ISO/IEC 39075's `<simple comment>` grammar ends it at either; NEL, LS
  and PS complete the set C# uses for its own line terminators.
- **Vertical tab and form feed are not terminators.** U+000B and U+000C are whitespace between
  tokens but stay inside a comment, as in PostgreSQL and C#.
- **Strings, quoted identifiers and block comments are unaffected.** A literal or a `/* */`
  comment may span lines, so a terminator inside it ends nothing, and `--` inside a literal starts
  no comment. The escape spelling `\r` or `\n`, written as two characters, is text everywhere.
- **`//` is not a comment in any language.** The lexer reads `/` as the division operator, so
  `//` is two `Slash` tokens and each parser rejects it (GQL with `GQL0002`). ISO/IEC 39075 also
  spells a `<simple comment>` with `//`; an item that adds that form needs a per-language lexer
  switch, because `//` must stay an error in SQL and OQL, and it ends the comment through the same
  scan and terminator set as `--`.
- **One line-numbering rule.** `TokenLexer.CountLineBreaks(ReadOnlySpan<char>)` counts each
  terminator as one line break, except that CR immediately followed by LF is one break together.
  OQL and GQL count diagnostic and expression lines with it, so a diagnostic on the token after
  `-- note<CR>` reports the next line, the same line the lexer started. SQL diagnostics locate by
  offset (`Sql.Language/docs/DESIGN.md`, "Positions are offsets"). A tool that maps those offsets
  to lines must break lines by the same rule, and a tool that cuts a script into statements must
  lex it with `TokenLexer` rather than scan for comments itself. Otherwise its lines disagree with
  the engines' diagnostics, or it cuts inside text an engine reads as a comment.
- **Consistency with #1101 and gql-label-direction (#1139).** Every terminator is whitespace to
  `char.IsWhiteSpace`, so outside a comment it separates tokens and is never `Unrecognized`; the
  stray-character rule is unchanged. The rule moves where a comment ends, never where it starts,
  so the coded diagnostic #1139 will add, keyed on a `--` comment beginning exactly where a node's
  `)` or an edge's `]` ends, holds whichever terminator ends the comment. Until it lands,
  `(a)-->(b)` followed by any line terminator reads as `(a)` plus a comment. Before #1150 a CR,
  NEL, LS or PS ending made such a GQL statement fail with `GQL0002`, because the comment swallowed
  the rest of the text; it now truncates silently, as an LF ending already did, so
  `MATCH (a:Person)-->(b:Person)<CR>DETACH DELETE a` deletes every Person
  (`Graph.Language/docs/DESIGN.md`, "Comment boundary"). #1139's cases must run at every
  terminator.
- **Tests.** `TokenLexerLineCommentTests` pins each terminator, CR LF, a comment at the end of
  the input, terminator escapes in literals and comments, and terminators inside string literals,
  quoted identifiers and block comments. Each language pins the boundary in its parser
  (`SqlLineCommentTests`, `OqlLineCommentTests`, `GqlLineCommentTests`), and
  `SqlStatementCompletenessTests` appends a comment plus leftover text at every terminator to
  every statement form. The SQL engine (`SqlLineCommentExecutionTests`, both session seams) and the
  wire (`SqlLineCommentWireTests`, `SqlDatabaseServer` and `Sql.Client`) show that `DELETE` and
  `UPDATE` with a `WHERE` after a comment change exactly one row.

## Non-goals

- No shared clause enum or universal grammar.
- No parser implementation for a particular database model.
- No reflection-based discovery or runtime registration of language profiles.

## AOT posture

Profiles use copied arrays, a comparer-backed set, and direct construction. Lexer projection and
diagnostic creation require no reflection, dynamic code generation, or dependency-injection stack.
The recognized-unsupported tables are static arrays scanned by ordinary loops. The line-terminator
set is one static `SearchValues<char>` built from a literal, which needs no reflection or runtime
code generation.
