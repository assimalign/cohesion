# Assimalign.Cohesion.Database.Graph.Language — Design

## Standard decision (#193)

The owner selected **ISO/IEC 39075:2024 GQL** on **2026-09-17**, following
`DATABASE_PROGRAM_PLAN.md`. ISO GQL provides a ratified, independently governed standard and a
portability target for the graph engine; openCypher's vendor-centered governance was the rejected
alternative. This records the owner decision independently of the feature list and chat transcript.
The GitHub issue may still require an administrative close; this implementation does not post to it.

[ISO's publication record](https://www.iso.org/standard/76120.html) identifies the first edition as
published in April 2024 and describes its property-graph data structures and operations. The
[standard editors' status page](https://www.gqlstandards.org/) records publication and the standards
committee process. These are the standard-selection references, not a claim that this MVP implements
every mandatory ISO feature.

The implemented syntax is an explicitly bounded subset. **`INSERT` is the standard graph-insertion
verb. `CREATE (pattern)` is a Cohesion compatibility extension**, retained for the phase-5 examples.
Both compile to the same `Creates` AST. Database-scoped catalog `SHOW` statements are another
explicit Cohesion extension. Repeated colon labels and `!=` are accepted conveniences: `:A:B`
builds the same tree as ISO `:A&B`, and mixing repeated colons with label-expression operators
(`:A:B|C`, `:A|B:C`) is `GQL0002`. Applications seeking portable syntax should write `:A&B` and
`<>`. Database, graph, schema, and session selection statements are outside the profile: all
evaluation stays inside the already-bound logical database.

## Parser and expression tree

`GqlQueryParser` derives from the shared `QueryParser`, and its `Profile` is
`GqlLanguageProfile.Instance`. The shared `TokenLexer` supplies graph arrows and punctuation. The
parser constructs immutable lists/maps in the AST and returns diagnostics on `GqlQueryStatement`.
Its partial files separate the statement, pattern, and scalar-expression grammars. Instances
serialize concurrent calls and clear their transient state between statements. Shared analyzers run
through the existing base-class pipeline.

The parser depends on shared language contracts; the graph planner depends on the resulting AST,
and the graph executor depends on the planner's plan. This dependency view summarizes that flow.

```mermaid
flowchart LR
    Parser["GQL parser"] --> Shared["Shared QueryParser and TokenLexer"]
    Parser --> Ast["GQL statement and pattern AST"]
    Planner["Graph planner"] --> Ast
    Executor["Graph executor"] --> Planner
```

`GqlQueryExpression` carries match paths, an optional predicate, insertion paths, deletion variables,
a detach flag, projections, and an optional `GqlCatalogSurface` for dedicated metadata statements.
Catalog statements cannot carry graph clauses. `GqlPathPattern.Variable` optionally names a MATCH
path; it is null for anonymous paths and insertion paths. Every path holds one more node than relationship. Anonymous nodes
and relationships have a null variable. Relationship directions are relative to consecutive pattern
nodes; incoming arrows reverse that relationship's endpoints. A scalar property reference always
contains a variable and exactly one property key; qualified database names have no AST representation.

A node or relationship pattern's label specification (`:` or `IS`, then a label expression) is the
init-only `LabelExpression`, a `GqlLabelExpression` tree of five sealed records: `GqlLabelName`,
`GqlLabelWildcard` (`%`), `GqlLabelNegation` (`!`), `GqlLabelConjunction` (`&`) and
`GqlLabelDisjunction` (`|`). Parentheses group without a node. `Labels` and `Type` are filled only
for a pure conjunction or a single type, so a hand-built AST that sets only those keeps its meaning;
when both are set, the planner requires `Labels` to name exactly the conjunction and `Type` to equal
the single name (`COHDBG001` otherwise). `GqlPatternDirection.LeftOrRight` is the ISO left-or-right
direction. A `WHERE` labeled predicate is `GqlLabeledPredicate` (variable, expression, `IsNegated`),
a Boolean primary beside the comparisons.

## Supported-clause matrix

Only these clauses occur in `GqlLanguageProfile.Clauses`. The larger reserved keyword table is
retained for tokenization and targeted unsupported diagnostics; it does not advertise capabilities.
The builtin-function table is empty because the executor implements no functions.

| Clause | Accepted subset | Execution |
| --- | --- | --- |
| `MATCH` | Comma-separated finite node/relationship chains; optional `variable =` path assignment, label expressions, literal property maps; every ISO full and abbreviated edge except the tilde forms | Planner chooses an indexed anchor when available; executor matches bounded relationship-unique trails and binds named paths in traversal order |
| `WHERE` | Scalar `=`, `<>`, `!=`, `<`, `<=`, `>`, `>=` comparisons and labeled predicates (`n IS [NOT] LABELED A`, `n:A`) joined by `AND`; predicate parentheses | Filters bound properties against literal or property operands, and bound elements by label expression |
| `RETURN` | Bound node/relationship/path variables or scalar properties, optional `AS` aliases | Projects elements or scalar values in source order; the engine path-request API requires exactly one bound entity or path projection |
| `INSERT` | Literal node/path insertion, optionally following a match; a node takes a label conjunction (`:A&B`, `:A:B`), a relationship one type and a directed edge (`-[:T]->`, `<-[:T]-`) | Inserts nodes and relationships transactionally |
| `CREATE` | Same insertion grammar as `INSERT`; compatibility extension | Same transactional insertion path |
| `DELETE` | Bound node/relationship variables following a match | Refuses deleting a node that still has incident relationships |
| `DETACH DELETE` | Bound node/relationship variables following a match | Deletes incident relationships with the node in one transaction |
| `SHOW` | `LABELS`, `RELATIONSHIP TYPES`, `PROPERTY KEYS`, `INDEXES`, or `OBJECT OWNERSHIP`; Cohesion extension | Returns typed, read-only metadata from the session database's catalog snapshot |
| `LABEL EXPRESSION` | ISO/IEC 39075 16.8 `\|`, `&`, `!`, `%` and parentheses after `:` or `IS` in a node or edge pattern; the labeled predicate in `WHERE` (#1139) | A node is tested against its label set, a relationship against its one type; a disjunction, negation, wildcard or labeled predicate never supplies the index anchor |

Match patterns accept `(a)`, `(a:Label {key: value})`, `(a IS A|B)`, `(a:(A|B)&!C)`, `(a:%)`, and
every ISO directed edge in full and abbreviated form: `-[r:TYPE]->` or `->`, `<-[r:TYPE]-` or `<-`,
`-[r:TYPE]-` or `-` (any direction), and `<-[r:TYPE]->` or `<->` (left or right), including anonymous
bracketed relationships and edge label expressions such as `-[r:T|U]->`. The lexer reads `<->` as
`<-` then `>`, and the parser joins the two only when they touch, so `(a)<- >(b)` and `(a)- >(b)` are
`GQL0002`. An abbreviated edge carries no variable, label expression or property map (`-r->`,
`-:T->`, `-{k: 1}->` are `GQL0002`). Scalar literals are null, Boolean, signed 64-bit integer,
finite double, and single-quoted string with doubled-quote escaping. Names are case-sensitive;
keywords are case-insensitive, and a label name may spell a keyword (`(n:Order)`, `n:A|Order`).
Double-quoted names preserve spaces. A single optional trailing semicolon is accepted. Multiple
statements, the tilde (undirected) edges, Cypher arrows, nested property access, and
return-after-delete are not accepted.

The grammar is intentionally finite:

```text
query        := [ MATCH match-paths [ WHERE predicate ] ]
                ( (INSERT | CREATE) paths [ RETURN projections ]
                | [ DETACH ] DELETE variables
                | RETURN projections )
match-paths  := match-path (',' match-path)*
match-path   := [ variable '=' ] path
paths        := path (',' path)*
path         := node (edge node)*
node         := '(' [ variable ] [ label-spec ] [ properties ] ')'
edge         := '-[' filler ']->' | '<-[' filler ']-' | '-[' filler ']-' | '<-[' filler ']->'
              | '->' | '<-' | '-' | '<->'
filler       := [ variable ] [ label-spec ] [ properties ]
label-spec   := (':' | IS) label-expr | ':' label ( ':' label )+
label-expr   := label-term ('|' label-term)*
label-term   := label-factor ('&' label-factor)*
label-factor := '!' label-primary | label-primary
label-primary:= label | '%' | '(' label-expr ')'
predicate    := comparison ('AND' comparison)*
comparison   := operand comparison-operator operand | labeled | '(' predicate ')'
labeled      := variable (IS [NOT] LABELED | ':') label-expr
operand      := variable '.' property | scalar-literal
projection   := variable ['.' property] [AS alias]
catalog      := SHOW (LABELS | RELATIONSHIP TYPES | PROPERTY KEYS | INDEXES | OBJECT OWNERSHIP)
```

The repeated-colon form of `label-spec` applies to node patterns only; a relationship has one type.
`LABELED` is positional, like the path mode words: only after `IS` or `IS NOT` in a predicate does
it introduce a label expression, so it remains usable as a name. `!` negates one primary, as ISO
writes it, so `!!A` is `GQL0002` and `!(!A)` parses.

A mutation can start without `MATCH`; a read or deletion must bind variables through `MATCH`.
Named assignment is a MATCH-only construct: `INSERT p = (...)` and `CREATE p = (...)` are
unsupported. `MATCH p = (a)-[r:TYPE]->(b) RETURN p` captures the entire matched trail, including
the order of nodes and relationships when an indexed anchor starts in the middle or a relationship
is traversed in reverse. A path variable cannot be deleted or used as a scalar property owner;
the planner rejects `DELETE p` and `RETURN p.name` rather than treating a path as an entity.
A named path cannot rebind an existing variable, including a node or relationship binding.
Pattern chains are limited to 64 relationships. Predicates are limited to 128 nesting levels and
128 comparisons and labeled predicates together, bounding left-associated conjunction trees as well
as parentheses. A label expression is limited to 128 levels of tree depth and 128 levels of
parentheses (`GQL0005`), and the planner applies the same tree bound to a hand-built AST
(`COHDBG001`), so evaluation recursion is bounded at 128. Match execution
also imposes a materialized-binding limit, documented in the Graph engine design. Quantified paths
are unsupported, so cycles cannot cause unbounded repetition of a path pattern. The executor's
trail rule permits repeated nodes but forbids repeated relationship identities within one path.

### Dedicated catalog statement choice (C2)

The separate `SHOW` grammar exposes metadata through the query language without constructing fake
nodes or relationships for `MATCH`. Catalog definitions have stable catalog identities but are not
graph elements. The extension adds no server scope or database selector and changes no public
interface. Keywords are case-insensitive and comments and one optional trailing semicolon follow
the ordinary lexer rules. A statement cannot combine `SHOW` with graph matching, projection or
mutation. Attempted mutation composition produces `GQL0007`, including verbs such as `SET` and
`DROP` that are otherwise unsupported. The engine validates direct AST requests too.

The [engine's catalog section](../../Assimalign.Cohesion.Database.Graph/docs/DESIGN.md#catalog-introspection-c2)
defines ordered result columns, SQL-consistent ownership vocabulary and session snapshot behavior.
The parser describes only the metadata subject; it never accesses or caches catalog state.

## Diagnostics and conformance (#194, #195)

| Code | Meaning |
| --- | --- |
| `COHDBL001` | Recognized construct outside the executable profile, reported once at its own span: optional matching, procedures, functions, parameters, collection literals, quantifiers, ordering, set operations, server/session scope, undirected (tilde) edges, path mode and path search prefixes, `NODETACH DELETE`, and `MERGE` |
| `GQL0001` | Empty statement |
| `GQL0002` | Malformed supported syntax, invalid statement composition, leftover tokens, or a character GQL does not use (`?`, `#`, `^`, `§`, ...) |
| `GQL0003` | Unterminated string, quoted name, or block comment |
| `GQL0004` | Invalid or out-of-range numeric literal |
| `GQL0005` | Pattern length, comparison count, or expression or label-expression nesting limit exceeded |
| `GQL0006` | Duplicate literal property key |
| `GQL0007` | `Graph catalog introspection is read-only.`: mutation composed with `SHOW` |
| `GQL0008` | A `--` comment begins exactly where a node pattern's `)` or an edge pattern's `]` ends, in `MATCH`, `INSERT` or `CREATE`: a Cypher arrow (`-->`, `--`) that would hide the rest of the line. Reported at the `--`; the message names `->`, `<-` and `-` |

Locations use zero-based absolute UTF-16 offsets with exclusive ends and one-based line numbers.
A line breaks at LF, CR, NEL (U+0085), LS (U+2028) or PS (U+2029), and CR LF is one break
(`TokenLexer.CountLineBreaks`), so lines break exactly where a `--` comment ends.
Capability checks run before syntax parsing, so a recognized unsupported clause does not degrade
into a generic error caused by its downstream syntax. Quoted text, comments, label names, and
property keys do not trigger keyword-based capability checks. Binding and catalog diagnostics belong
to the Graph planner, which knows the database schema.

### Keyword disposition (#1101)

The capability scan reads one static recognized-unsupported table, `GqlUnsupportedVocabulary`.
Each entry has a spelling, the construct its `COHDBL001` names, and a position. Each construct is
reported once, at its own span:

| Written | Construct named | Span | Who lifts it |
| --- | --- | --- | --- |
| `(a)~[r]~(b)`, `<~[r]~`, `~[r]~>`, `(a)~(b)`, `<~`, `~>` | `UNDIRECTED EDGE` | the whole edge, from `~` or `<~` through its closing `~` or `~>` | pin until the engine stores undirected edges |
| `MATCH TRAIL (a)-[]->(b)`, `MATCH p = ACYCLIC (...)`, `WALK`, `SIMPLE` | `<MODE> PATH MODE` | the mode word, plus `PATH`/`PATHS` | pin |
| `MATCH ALL SHORTEST (...)`, `MATCH ANY SHORTEST (...)` | `ALL SHORTEST`, `ANY SHORTEST` | the prefix, plus its path mode and `PATH`/`PATHS` | pin |
| `MATCH SHORTEST 2 (...)`, `SHORTEST 2 GROUPS` | `SHORTEST PATH` | the prefix, plus its count, path mode and `PATH`/`PATHS` or `GROUP`/`GROUPS` | pin |
| `MATCH ALL (...)`, `MATCH ALL TRAIL (...)`, `MATCH ANY 2 (...)` | `ALL PATH SEARCH`, `ANY PATH SEARCH` | the prefix, plus its count, path mode and `PATH`/`PATHS` | pin |
| `MATCH (n) NODETACH DELETE n` | `NODETACH DELETE` | both words | pin |
| `MERGE (n)` | `MERGE` | `MERGE` | pin |

#1101 pinned the label-expression operators (`LABEL DISJUNCTION`, `LABEL CONJUNCTION`,
`LABEL NEGATION`, `WILDCARD LABEL`) and `(n IS A)` (`IS LABEL EXPRESSION`) here; #1139 made them
executable, removed them from the table and flipped their corpus cases to supported parses. A
`[:A|:B]` relationship alternative, which the old pin also covered, is `GQL0002` now: ISO writes
`[:A|B]`. A path mode word
was read as a path variable, so `MATCH TRAIL (...)` failed with "Expected '='". `ALL SHORTEST`
reported two diagnostics, `ALL` and `SHORTEST PATH`, and a path mode after a path search prefix
(`ALL TRAIL`, `ANY SHORTEST TRAIL`) was a second construct; ISO/IEC 39075 makes the whole
`<path search prefix>` one. `NODETACH` failed with
"MATCH requires RETURN, INSERT, CREATE, or DELETE.". `STARTS WITH` and `ENDS WITH` are one
construct each, and a procedure's `YIELD` belongs to its `CALL`. A statement whose first word is
unsupported, such as `SESSION SET GRAPH g` or `DROP GRAPH g`, is one construct: the scan
reports that word and nothing after it.

Path mode words and `NODETACH` are positional, not lexer keywords. Before a path pattern, or
before `DELETE`, they name the construct. Elsewhere they are names, so `MATCH trail = (a)-[]->(b)`
and `MATCH (nodetach) DELETE nodetach` still parse.
`GqlKeywordDispositionTests` enumerates the profile's keywords, the table, and the executable
label-expression spellings `GqlLabelVocabulary` lists (`|`, `&`, `!`, `%` and the positional
`LABELED`). Every word needs a supported parse case or a case with exactly one `COHDBL001` naming its
construct, and for a table entry that diagnostic must also name the construct the table records. A
word added without a case fails. `~` is a table entry (`UNDIRECTED EDGE`); `--` after a pattern
element is `GQL0008`, which "Label expressions and edge directions" below describes.

A character GQL does not use lexes as `TokenType.Unrecognized`. It reports
`Unexpected character '<c>'; it is not part of GQL.` (`GQL0002`) at its span during
tokenization, and the statement is not parsed, so nothing is bound. Every statement form
(`MATCH ... RETURN`, `INSERT`, `CREATE`, `DELETE`, `DETACH DELETE`, `SHOW`) rejects leftover
tokens with `GQL0002`, with or without a separating `;`, and `GqlParseStrictnessTests` pins each.

`GqlQueryParserTests` is the executable-subset conformance corpus, with valid and malformed cases
and assertions on the AST, not a full ISO certification suite. Its groups map to ISO GQL graph
pattern matching (4.11 / 16.4 / 16.7), insertion (13.2), deletion (13.5), matching (14.4), return
(14.11), comparisons (19.3), and property references (20.11). Label expressions (16.8) and the
labeled predicate have their own corpus, `GqlLabelExpressionParserTests`, and the 16.7 edge forms
`GqlEdgeDirectionParserTests`. The ISO section mapping is independently
cross-checkable in an implementer's [published GQL feature table](https://neo4j.com/docs/cypher-manual/25/appendix/gql-conformance/supported-mandatory/).
Compatibility-extension cases are named by their `CREATE` spelling. Separate tests pin the exact
profile, reject unsupported and cross-database constructs, preserve source locations, and bound
adversarial nesting and long patterns.

### Comment boundary (#1150)

A `--` comment ends before the first line terminator: LF, CR, NEL (U+0085), LS (U+2028) or PS
(U+2029), with CR LF read as one break, the rule `Database.Language/docs/DESIGN.md` records for
every language. ISO/IEC 39075's `<simple comment>` ends at CR or LF. The shared lexer used to end it
only at LF, so in `MATCH (a:Person) -- note<CR>WHERE a.name = 'x'<LF>DETACH DELETE a` the `WHERE`
was comment text and every Person was deleted. (With `DETACH DELETE` on the CR line as well, the
comment swallowed it too and the statement failed with `GQL0002`.) The `WHERE` now stays in effect
at every terminator. `GqlLineCommentTests` pins both delete forms, the clause after a spaced
comment (`MATCH (a) -- c<CR>RETURN a`, the ISO case #1139 keeps), and diagnostic lines at each
terminator.

The rule moves only where a comment ends. `GQL0008` (#1139) keys on where a `--` comment starts,
so it holds whichever terminator ends the comment. Between #1150 and #1139, `(a)-->(b)` followed
by any line terminator read as `(a)` plus a comment, so
`MATCH (a:Person)-->(b:Person)<CR>DETACH DELETE a` ran as `MATCH (a:Person) DETACH DELETE a` and
deleted every Person. It is now `GQL0008` at every terminator, and `GqlCypherArrowTests` and the
engine's `GqlLabelDirectionExecutionTests` pin it at each one.

ISO also spells a simple comment `//`. The shared lexer has no `//` comment, so
`MATCH (a) // note` reports `GQL0002` at the first `/`. An item that adds the `//` form adds a
per-language lexer switch and ends the comment at the same terminators.

## Label expressions and edge directions (#1139)

The profile advertises `LABEL EXPRESSION`: ISO/IEC 39075 16.8 label expressions in node and edge
patterns, G074's wildcard `%`, and the `<labeled predicate>` in `WHERE`. The parser binds `!`
tighter than `&` and `&` tighter than `|`; both binary operators associate to the left. The
engine evaluates a node's expression against its label set and a relationship's against its one
type, with a typed switch over the five records and no reflection or text matching:

| Expression | A node matches when | A relationship matches when |
| --- | --- | --- |
| `A` | it carries `A` | its type is `A` |
| `%` | it carries at least one label | always: every stored relationship has a type |
| `!e` | it does not match `e`, so `!%` selects unlabeled nodes | it does not match `e`, so `!%` selects none |
| `e & f` | it matches both | it matches both, so `T&U` selects none |
| `e \| f` | it matches either | it matches either |

Every name in a `MATCH` expression must be a catalog label or relationship type, including names
under `!` and `|` (`COHDBG002`), because labels are persistent catalog definitions. An `INSERT`
node takes a pure conjunction (`:A`, `:A&B`, `:A:B`) and may introduce new labels; `|`, `!` and
`%` select nodes and cannot label a new one, so they are `COHDBG001`. An inserted relationship
needs one type. A labeled predicate (`n IS LABELED A|B`, `n IS NOT LABELED A`, `n:A|B`) resolves
its names against the catalog of the variable's kind, a path variable is `COHDBG003`, and an
unbound one `COHDBG001`; when gql-optional-match lands, a variable without a binding yields
UNKNOWN. Whichever of this item and gql-where-expr lands second adds the `NOT`/`OR` combinations
with labeled predicates. A `:` directly after an element variable in a predicate is the labeled
predicate; an operand that begins with `:` keeps shared-parameters' coded `:name` rejection, and
whichever of the two lands second adds the other's case.

**Index anchors.** The planner anchors only on `Labels`, which the parser fills only for a pure
conjunction: every match must carry each listed label, so anchoring on any one of them drops no
row. A disjunction, negation or wildcard leaves `Labels` empty, and a labeled predicate is never an
equality, so neither feeds an anchor: `(n:A|B {k: 1})`, `(n:!A {k: 1})` and
`MATCH (n) WHERE n:A AND n.k = 1` scan, and the executor tests each candidate. `(n:A&B {k: 1})`
anchors on an index of `A` or `B`.

**Directions.** Storage holds only directed relationships, so the ISO forms map as follows:

| Written | `GqlPatternDirection` | Matches | Inserts |
| --- | --- | --- | --- |
| `-[]->`, `->` | `Outgoing` | the stored edge from the preceding node | yes, with one type |
| `<-[]-`, `<-` | `Incoming` | the stored edge into the preceding node | yes, with one type |
| `-[]-`, `-` | `Undirected` (any direction) | either orientation | `COHDBG001` |
| `<-[]->`, `<->` | `LeftOrRight` | either orientation, exactly as `Undirected` | `COHDBG001` |
| `~[]~`, `<~[]~`, `~[]~>`, `~`, `<~`, `~>` | none | `COHDBL001` `UNDIRECTED EDGE` | `COHDBL001` |

One stored `A`-to-`B` edge therefore yields the rows `(A,B)` and `(B,A)` for `-[r]-`, `-`, `<-[r]->`
and `<->`, and a self-loop yields one row. The tilde forms are rejected rather than matched: `~[]~`
and `~` would match nothing, and `<~[]~` and `~[]~>` would silently equal their directed forms. The
rejection applies only in pattern position, directly after a node's `)`; a `~` elsewhere is a
character GQL does not use (`GQL0002`). Insertion rejects both either-direction forms, because
the executor would otherwise have to pick an orientation.

**Cypher arrows (D3).** ISO/IEC 39075 defines `--` as a `<simple comment>`, and the shared lexer
reads it that way in every language, so no lexer file changes and SQL and OQL comments cannot
regress. The parser records where each `--` comment starts and, where a node pattern consumes its
`)` or an edge pattern its `]`, reports `GQL0008` at the `--` when a comment starts at exactly that
offset. That turns the silent truncation `MATCH (b:Person)-->(a)<LF>RETURN b` →
`MATCH (b:Person) RETURN b` into an error at every line terminator. Comments elsewhere keep their
ISO meaning: `(a) -- note`, a comment on its own line, and `WHERE (a.age > 1)-- note`, whose `)`
closes a predicate. Under D3 these Cypher spellings have no 1:1 ISO counterpart and are not
accepted:

| Cypher | Read by GQL as | Result | ISO spelling |
| --- | --- | --- | --- |
| `(a)-->(b)` | `(a)` then a comment | `GQL0008` | `(a)->(b)` |
| `(a)--(b)` | `(a)` then a comment | `GQL0008` | `(a)-(b)` |
| `(a)-[r]-->(b)`, `(a)-[r]--(b)` | `-[r]` then a comment | `GQL0008` | `-[r]->`, `-[r]-` |
| `(a)<--(b)` | `<-` then `-` | `GQL0002` | `(a)<-(b)` |
| `(a)<-->(b)` | `<-` then `->` | `GQL0002` | `(a)<->(b)` or `(a)-(b)` |

The residual is recorded rather than guessed at: whitespace or a block comment between the element
and the dashes, as in `(a) -->(b)` or `(a)/* c */-->(b)`, leaves an ISO comment, so the rest of
that line is still comment text. `GqlCypherArrowTests` pins that residual so a change to it is
deliberate.

## AOT posture and extension discipline

The package references only `Database.Language`. It uses ordinary typed AST classes and records
and BCL scalar values with no reflection, Regex, runtime discovery, or `Microsoft.Extensions.*`
dependencies; `GqlLabelExpression.ToString` renders GQL text with a type switch. Adding a
clause requires the parser, planner, executor, conformance cases, and this matrix in the same change;
keyword recognition alone never enables it.
