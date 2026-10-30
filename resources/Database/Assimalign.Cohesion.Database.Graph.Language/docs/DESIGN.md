# Assimalign.Cohesion.Database.Graph.Language — Design

## Savepoint grammar and rendering (#1726, XT-4)

Standalone named controls retain TransactionCommand and SavepointName. The name reuses the
existing standalone body reference, preserving every ordinary GqlQueryExpression allocation.
Three profile clauses and parser/keyword conformance corpora advance together; no lexer keyword
or graph expression syntax changes. SQL savepoint forms are documented Cohesion extensions.
See DIALECT for scope lifetime, failure rules and the in-process/wire conformance corpus.
The parser and renderer corpus covers both TO forms with no modifier or WORK and TRANSACTION (PostgreSQL src/backend/parser/gram.y:11414-11431,11483-11486).

## SQL operator isolation (#1721)

The shared lexer is unchanged. The operator corpus pins # as Unrecognized, ~, &, |
and paired angle brackets as their existing tokens, with SQL bitwise syntax still refused.
GQL additionally pins :A|B, :A&B, ! and <-, ->, <--, -->, <-[ boundaries.
Profile conformance also pins :: as ColonColon, separated
colons as individual Colon tokens, and `~~*` and `!~~*` as individual tilde/bang/asterisk tokens.
SQL casts do not become expression syntax here; adjacent colons preserve the existing
parser refusal and GQL label grammar. No language vocabulary or persisted text changes.

## Left-arrow tokenization (#1647)

`GqlLanguageProfile` explicitly enables the shared `RecognizeLeftArrow` option. Incoming
`<-`/`<-[r]-`, left-or-right `<->`/`<-[r]->`, and outgoing `->`/`-[r]->` keep their existing
pattern directions, pinned by `GqlLeftArrowConformanceTests`. SQL/OQL profiles leave the option
disabled; no language-name check or alternate lexer is introduced.

## Named scalar parameters (#1102, #1138)

The [shared parameter contract](../../Assimalign.Cohesion.Database.Language/docs/DESIGN.md#parameter-contract-1102)
governs `$name` and `@name`: strip one sigil from references and map keys, then match bare names
ordinally and case-sensitively, independently of the caller's dictionary comparer. `$1` binds
the exact key `"1"`; GQL has no positional binding. `$name` is the ISO and Cypher spelling;
`@name` is a Cohesion cross-language spelling, neither ISO nor Cypher. Numeric names contain
ASCII digits only. Bare `$`/`@`, numeric-name junk, and parameters in name or DELETE positions
are CG00002. Operand `?` and `:name` retain #1102's supported-form message and complete span.
References are validated before projection or whole-map capability checks: a bare sigil in
`RETURN $`, `RETURN @`, `(n $)` or an edge filler is CG00002 at that sigil. Valid references
in those unsupported positions retain their named CK00001 capability diagnostic.

`GqlParameterExpression` exposes the bare `Name` and complete reference `Location`.
**Breaking AST change:** `GqlNodePattern.Properties` and `GqlRelationshipPattern.Properties`
are now `IReadOnlyDictionary<string, GqlExpression>`. Each map entry is a scalar value expression; hand-built ASTs must wrap literals.
WHERE accepts parameters wherever it accepts scalar operands, including IN list items.

Planning snapshots and normalizes the supplied map, rejects duplicate normalized keys with
CG01014 even if unused, and validates every referenced value before reading data or taking
the writer lock. Missing bindings use CG05001, including under EXPLAIN and in skipped Boolean
branches or empty matches. Supplied null is bound; unused entries need no value conversion.
Values retain their CLR runtime scalar type and use `GraphDatabase.TypeOf` promotion for
schema matching, as the typed API does: Int32 remains Int32 on read-back. Decimal is supported
through parameters and compares numerically with integers. Only null, Boolean, string, integral,
Decimal and finite floating values are supported. List and map parameters are admitted in value positions by #1146 below. Unsupported or
non-finite leaf values use CG03003. Pattern property maps remain scalar-only. No reflection or
runtime code generation is involved.

`$$name` (ISO substituted reference) and whole-map `(n $props)` remain CK00001 naming the
form. `RETURN $p` is a live value expression since #1141; paging parameters execute in #1142's
shared ordering/paging stage. #1146 lifts list/map parameters in value positions; property maps remain scalar.
`GqlParameterParserTests`, `GraphParameterTests`, the clause execution corpus and client/server
wire tests pin this boundary. Parameters add no profile clause.

Local Neo4j reference (behavior consulted, no code copied), relative to
`C:/Source/repos/neo4j/neo4j`: `community/cypher/runtime-util/src/main/scala/org/neo4j/cypher/internal/runtime/createParameterArray.scala:29-44`
checks name presence separately from a supplied null and retains the retrieved value;
`community/cypher/interpreted-runtime/src/main/scala/org/neo4j/cypher/internal/runtime/interpreted/commands/expressions/ParameterFromSlot.scala:27-29`
reads that bound value during evaluation. Cohesion additionally requires the shared named-map
contract and validates scalars while planning.

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
Both compile to an insertion stage. Database-scoped catalog `SHOW` statements are another
explicit Cohesion extension. Repeated colon labels and `!=` are accepted conveniences: `:A:B`
builds the same tree as ISO `:A&B`. The convenience repeats plain names only, so mixing repeated
colons with label-expression operators or a wildcard (`:A:B|C`, `:A|B:C`, `:%:A`, `:A:%`) is
`CG00002`. Applications seeking portable syntax should write `:A&B` and
`<>`. Database, graph, schema, and session selection statements are outside the profile: all
evaluation stays inside the already-bound logical database.

## Schema DDL (#1738, GQ-D2)

Standalone CREATE/DROP INDEX and CONSTRAINT plus Cohesion LABEL/RELATIONSHIP TYPE definition
commands use GqlSchemaCommand, GqlSchemaObjectKind and GqlSchemaPropertyType. The FOR variable
must match its property reference (CG01022). The dedicated parser branch skips ordinary-query
unsupported-function discovery, preserving one CK00001 for T3 forms and removing ON noise.
SchemaCommand.ToString renders semantic fields with escaped double-quoted identifiers and
canonical scalar names; it never depends on retained source text. The command is an optional
query-expression property stored in the existing body slot alongside the mutually exclusive
pipeline and composite forms, preserving ordinary query-expression allocation size. Malformed
hand-built combinations retain both operands only for the existing execution refusal. DIALECT specifies syntax,
conditionals, naming, transaction policy and token lifetime; Graph DESIGN owns coordination.

Relationship schema patterns accept optional incoming and outgoing arrows, including both,
and render as undirected patterns because constraints apply to a relationship type regardless
of direction. Relationship indexes retain one named CK00001 without syntax diagnostics. This
follows the local Neo4j grammar at
`C:/Source/repos/neo4j/neo4j/community/cypher/front-end/parser/v25/parser/src/main/antlr4/org/neo4j/cypher/internal/parser/v25/Cypher25Parser.g4:1202`.

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

`GqlQueryExpression.Stages` carries source-ordered MATCH, LET, FILTER, FOR/UNWIND, INSERT/CREATE, MERGE, SET, REMOVE, DELETE, RETURN/WITH and NEXT nodes. `Segments` splits that list at explicit NEXT boundaries. The original constructors retain a single-segment facade for hand-built ASTs. Stage records retain Cypher spellings for conformance checks; the base constructor is private protected, following the Database concrete-first variant rule.

The flat single-segment facade carries ordered `GqlMatchClause(Paths, Predicate, Optional)` records, insertion paths, deletion variables,
a detach flag, expression projections, a `GqlSetQuantifier` (ALL by default), `ReturnStar`, and an optional `GqlCatalogSurface` for dedicated metadata statements.
Catalog statements cannot carry graph clauses. `GqlPathPattern.Variable` optionally names a MATCH
path; it is null for anonymous paths and insertion paths. A path without parenthesized elements holds one more node than relationship. Anonymous nodes
and relationships have a null variable. Relationship directions are relative to consecutive pattern
nodes; incoming arrows reverse that relationship's endpoints. A scalar property reference always
contains a variable and exactly one property key; qualified database names have no AST representation.

The engine reads `Stages`; `MatchClauses` remains a flattened inspection view. The original constructor constructs one mandatory clause
when paths or a predicate are supplied, and none for an empty match without a predicate. `Matches`
and `Predicate` expose the single mandatory clause only, returning empty/null for any optional or
multi-clause shape. This implements the owner's AST default for hand-built statements; malformed
clause/path lists are refused by the planner with CG05001.

A node or relationship pattern's label specification (`:` or `IS`, then a label expression) is the
init-only `LabelExpression`, a `GqlLabelExpression` tree of five sealed records: `GqlLabelName`,
`GqlLabelWildcard` (`%`), `GqlLabelNegation` (`!`), `GqlLabelConjunction` (`&`) and
`GqlLabelDisjunction` (`|`). The conjunction and disjunction are n-ary: each holds an `Operands` list
of any length, so `A|B|C` and `:A:B:C` are one node each (see "Chain length and nesting" below).
Parentheses group without a node. `Labels` and `Type` are filled only
for a pure conjunction or a single type, so a hand-built AST that sets only those keeps its meaning;
when both are set, the planner requires `Labels` to name exactly the conjunction and `Type` to equal
the single name (`CG05001` otherwise). `GqlPatternDirection.LeftOrRight` is the ISO left-or-right
direction. A `WHERE` labeled predicate is `GqlLabeledPredicate` (variable, expression, `IsNegated`),
a Boolean primary beside the comparisons. A `WHERE` conjunction is one n-ary `GqlLogicalExpression`
(`Operator` And, Or or Xor, `Operands`); `GqlBinaryExpression` holds a comparison or arithmetic/concatenation, and the
planner rejects a binary node whose operator is `AND` (`CG05001`).

The label records compare structurally: two trees are equal when they have the same kinds in the
same shape, names equal ordinally and chains with the same operands in the same order, and
`GetHashCode` agrees. `ToString`, equality and hashing walk the tree with an explicit stack, so they
hold for a hand-built tree of any depth. The public chain constructors copy their operand sequence;
only the parser hands a list it built to a node without copying it.

## MERGE (#1735)

GqlMergeStage carries one Path and ordered OnCreate/OnMatch item lists. The parser accepts either
branch order with repeated branches; each kind's SET items append in source order. ON remains
contextual so existing variable names are preserved.
GqlMergeStage.ToString renders CREATE before MATCH using the
existing path/scalar renderer. Both branches share ordinary SET grammar. A MERGE accepts one path;
comma-separated independent MERGEs use successive clauses. Variable-length/quantified paths,
named path assignments, path modes/selectors, parenthesized update groups and inline predicates
remain refused by update-pattern validation. The engine validates creation-compatible labels,
relationship types, incoming-only equality expressions and both branch scopes before execution,
including EXPLAIN. Already-declared relationship variables are refused (CG03007); a repeated or
already-bound node must be a bare variable (CG03006). New relationships require one type.
Property positions parse the complete expression before admission. Direct list/map literals,
comprehensions and slices that still yield a container are refused by the parser with CK00001,
including inside MERGE patterns and literal-map updates. Indexing or field access that yields a
scalar is accepted. The CK00001 diagnostic retains the established list/map storage-refusal
wording and spans the completed expression. Other proven or evaluated invalid MERGE equalities use CG03004; ordinary
property writes retain CG03003. Required properties can be supplied by ON CREATE before the
finished created entity is validated. Row-dependent equalities can use a per-row index seek.
GqlMergeParserTests and the profile execution corpus pin grammar, rendering and live behavior.

## SET and REMOVE (#1149)

SET/REMOVE recognize `IS` as a label assignment only immediately after an item target.
Scalar SET values retain `IS [NOT] LABELED`, including contextual label names such as Graph and Schema.

`GqlSetStage` and `GqlRemoveStage` carry ordered public item variants for scalar properties,
literal-map replacement/merge and node labels. `SetItems` and `RemoveItems` provide flattened
inspection facades. ISO sections 13.3 and 13.4 supply property, all-property and label items;
`+=` and multi-label colon lists are D3 spellings. The parser combines touching `+=` into one
GQL token; SQL/OQL lexer behavior is unchanged. GRAPH and SCHEMA are contextual label names.
The private token uses a parser-only spelling and is consumed only by whole-map SET, so it
cannot become unary addition, property assignment or a quantified path elsewhere.
Whole-map parameters and expressions remain CK00001, as do statically composite property values.
MERGE embeds both SET branch bodies using the same parser and update validation (#1735).
Leading SET/REMOVE and nested assignment targets are CG00002. See [DIALECT.md](DIALECT.md#set-and-remove)
for row ordering, NULL, no-op and count semantics. `GqlUpdateParserTests` pins the AST and
diagnostics; the live profile corpus covers node properties/labels and relationship properties
for both clauses, followed by verification reads. EXPLAIN describes each update target.

## Supported-clause matrix

Only these clauses occur in `GqlLanguageProfile.Clauses`. The larger reserved keyword table is
retained for tokenization and targeted unsupported diagnostics; it does not advertise capabilities.
The function table advertises the aggregates and core scalar functions listed below, including their accepted aliases.

| Clause | Accepted subset | Execution |
| --- | --- | --- |
| `MATCH` | Comma-separated paths with fixed/quantified edges and parenthesized segments; optional `variable =` path assignment, label expressions, scalar expression property maps; every ISO full and abbreviated edge except the tilde forms | Planner chooses an indexed anchor when available; executor applies the outer path mode (implicit TRAIL) and binds named paths in traversal order |
| `OPTIONAL MATCH` | The same graph patterns and clause-local WHERE, including a leading optional or several consecutive matching clauses | Left join against each running binding; an unmatched clause preserves that row and binds its new variables to NULL |
| `PATH SELECTOR` | ANY, ALL, ANY/ALL SHORTEST, SHORTEST k [PATHS], SHORTEST k GROUPS | Selects independently per endpoint pair and input row; deterministic identity ties, prefilters inside search and ordinary WHERE after selection |
| `PATH MODE` | WALK, TRAIL, SIMPLE or ACYCLIC after an optional path assignment, with optional PATH/PATHS | Applies to the complete path; nested explicit modes and unselected unbounded WALK remain CK00001 |
| `QUANTIFIED PATH` | ISO edge/subpath `{m,n}`, `{m}`, `{m,}`, `{,n}`, `{,}`, `+`, `*`; Cypher bracketed `*m..n` | Enumerates paths under the outer mode, including zero-length matches; group variables bind source-ordered lists |
| `EXISTS` | `EXISTS { MATCH pattern [WHERE predicate] ... }` or `EXISTS { pattern [, pattern] [WHERE predicate] }`; scalar NOT supplies NOT EXISTS | Correlated semi/anti apply, isolated scope, Boolean result and first-match exit |
| `WHERE` | Boolean and scalar expressions: comparisons, labels, AND/OR/XOR/NOT, IS null/truth tests, arithmetic, concatenation, IN lists and string predicates | Keeps TRUE only; see [DIALECT.md](DIALECT.md) and scalar expressions below |
| `EXPLAIN` | One supported linear or composite statement | Shared description rows without executing any read expansion or write; Cohesion extension |
| `LET` | One or more `variable = expression` assignments | Adds new value bindings in assignment order; rebinding is CG03003 |
| `FILTER` | Predicate, optionally preceded by WHERE | Keeps TRUE only; FALSE and UNKNOWN drop the row |
| `FOR` | `FOR variable IN expression` | Expands a list; NULL yields zero rows; a non-list is CG02013 |
| `UNWIND` | Cypher spelling `UNWIND expression AS variable` | The same list expansion, with the scalar divergence below |
| `NEXT` | Separates linear segments after RETURN or a modifying segment | Carries projected columns, or one empty binding after a modifying segment without RETURN |
| `WITH` | Cypher projection spelling, including DISTINCT/ALL, star, GROUP BY, ordering/paging and WHERE | Projects a new scope; applies WHERE after ordering/paging |
| `UNION [DISTINCT]` | Complete read-only operands with matching columns | Existing structural tuple equality removes duplicates; all operands are planned first |
| `UNION ALL` | The same operands, without mixing modes | Streams every occurrence in operand order into the ordinary final result collector |
| `EXCEPT [DISTINCT/ALL]` | Read-only operands with matching columns/shapes | Distinct difference or max(m-n,0) occurrences |
| `INTERSECT [DISTINCT/ALL]` | Read-only operands with matching columns/shapes | Distinct intersection or min(m,n) occurrences |
| `OTHERWISE` | Both operands validated before execution | Whole-result fallback; the right runs only on empty left |
| `RETURN` | Optional DISTINCT/ALL, either `*` or value expressions with optional `AS` aliases; also standalone | Projects expressions, expands named bindings in declaration order, then eliminates duplicate tuples for DISTINCT; path requests require one entity or path column |
| `GROUP BY` | ISO names of non-aggregated RETURN items, equivalent input expressions, or the empty grouping set `()`; omission uses the same implicit keys | Bounded hash grouping before projection, DISTINCT, ordering and paging; aggregate placement and scope are validated before execution |
| `ORDER BY` | One or more expression keys with optional ASC/ASCENDING/DESC/DESCENDING and NULLS FIRST/LAST after RETURN or WITH | Orders projected rows and paths by the total order below, after DISTINCT |
| `OFFSET` | Unsigned Int64 literal or named parameter after RETURN | Drops the first count rows after ordering |
| `SKIP` | Cypher spelling of OFFSET | The same offset stage; cannot accompany another OFFSET/SKIP |
| `LIMIT` | Unsigned Int64 literal or named parameter after RETURN | Unordered reads can stop upstream expansion after OFFSET/LIMIT; ordered finite pages retain OFFSET + LIMIT rows; never reduces mutation inputs |
| `CASE` | Simple and searched CASE, COALESCE and NULLIF case abbreviations | First matching alternative, lazy selected branch; absent ELSE yields NULL |
| `PATTERN COMPREHENSION` | `[ [p =] pattern [WHERE predicate] \| projection ]` | Collects every inner match in order through the correlated-subplan boundary |
| `LIST COMPREHENSION` | `[x IN list [WHERE predicate] [| projection]]` | Lexical ordered filtering/projection; NULL list returns NULL |
| `LIST SLICING` | `list[from..to]`, either bound optional, at least one required | Inclusive start, exclusive end, negative offsets and clamped bounds |
| `REDUCE` | `reduce(acc = init, x IN list | expression)` | Ordered, type-changing accumulator; NULL source returns NULL and empty source returns seed |
| `LIST PREDICATE` | `all`, `any`, `none`, `single` with `(x IN list WHERE predicate)` | Lexical iterator, three-valued tests, empty identities and decisive short-circuiting |
| `INSERT` | Scalar value-expression node/path insertion, optionally following a match; a node takes a label conjunction (`:A&B`, `:A:B`), a relationship one type and a directed edge (`-[:T]->`, `<-[:T]-`) | Inserts nodes and relationships transactionally |
| `CREATE` | Same insertion grammar as `INSERT`; compatibility extension | Same transactional insertion path |
| `MERGE` | One complete fixed path, followed by zero or more ON CREATE SET or ON MATCH SET actions | Matches every complete path or creates every unbound part; includes fixed undirected relationships, bound node reuse, overlays and Snapshot freshness checks |
| `SET` | ISO property assignment, literal-map replacement and node labels with IS or colon; D3 `+=` and repeated-colon labels | Applies items left to right per row, with one published replacement per updated entity per statement |
| `REMOVE` | Property keys or node labels with IS or colon | Removes keys/labels, ignoring OPTIONAL NULL targets |
| `DELETE` | Bound node/relationship variables following a match | Refuses deleting a node that still has incident relationships |
| `DETACH DELETE` | Bound node/relationship variables following a match | Deletes incident relationships with the node in one transaction |
| `SHOW` | `LABELS`, `RELATIONSHIP TYPES`, `PROPERTY KEYS`, `INDEXES`, `CONSTRAINTS`, or `OBJECT OWNERSHIP`; Cohesion extension | Returns typed, read-only metadata from the session database's catalog snapshot |
| `CREATE/DROP INDEX` | Standalone Neo4j-style single node-property index commands, optional names and conditionals | Transactional discovery and physical/catalog coordination |
| `CREATE/DROP CONSTRAINT` | Named type/existence rules for one node or relationship property | Independent rules; validates existing owner data |
| `CREATE/DROP LABEL` | Cohesion definition commands with optional conditionals | Creates an empty definition or drops an unused one |
| `CREATE/DROP RELATIONSHIP TYPE` | Cohesion definition commands with optional conditionals | Creates an empty definition or drops an unused one |
| `LABEL EXPRESSION` | ISO/IEC 39075 16.8 `\|`, `&`, `!`, `%` and parentheses after `:` or `IS` in a node or edge pattern; the labeled predicate in `WHERE` (#1139); chains of any length and nesting the stack allows | A node is tested against its label set, a relationship against its one type; pattern and positive conjunctive WHERE labels can anchor scans or property seeks (#1245) |

Match patterns accept `(a)`, `(a:Label {key: value})`, `(a IS A|B)`, `(a:(A|B)&!C)`, `(a:%)`, and
every ISO directed edge in full and abbreviated form: `-[r:TYPE]->` or `->`, `<-[r:TYPE]-` or `<-`,
`-[r:TYPE]-` or `-` (any direction), and `<-[r:TYPE]->` or `<->` (left or right), including anonymous
bracketed relationships and edge label expressions such as `-[r:T|U]->`. The lexer reads `<->` as
`<-` then `>`, and the parser joins the two only when they touch, so `(a)<- >(b)` and `(a)- >(b)` are
`CG00002`. An abbreviated edge carries no variable, label expression or property map (`-r->`,
`-:T->`, `-IS T->`, `-{k: 1}->` are `CG00002`; the capability scan reads the names after such a
`:` or `IS` as labels, so neither `IS` nor a keyword-spelled label reports `CK00001` there). Scalar literals are null, Boolean, signed 64-bit integer,
finite double, and single-quoted string with doubled-quote escaping. Names are case-sensitive;
keywords are case-insensitive, and a label name may spell a keyword (`(n:Order)`, `n:A|Order`).
Double-quoted names preserve spaces and decode doubled `""` to one embedded double quote
(#1235). Label-expression rendering and the engine's source-free projection renderer double
embedded quotes, preserving parse/render/parse identity. This uses the shared lexer rule in
Database.Language; `GqlDelimitedIdentifierTests` and the live INSERT clause corpus cover it.
A single optional trailing semicolon is accepted. Multiple
statements, the tilde (undirected) edges, Cypher arrows, nested property access, and
return-after-delete are not accepted.

The executable grammar is:

```text
statement    := (query | EXPLAIN query | catalog) [';']
query        := segment (NEXT segment)*
segment      := stage* [RETURN projections return-tail]
stage        := [OPTIONAL] MATCH match-paths [WHERE predicate]
              | LET variable '=' predicate (',' variable '=' predicate)*
              | FILTER [WHERE] predicate
              | FOR variable IN predicate
              | UNWIND predicate AS variable
              | (INSERT | CREATE) paths
              | MERGE path (ON (CREATE | MATCH) SET set-items)*
              | SET set-item (',' set-item)*
              | REMOVE remove-item (',' remove-item)*
              | [DETACH] DELETE variables
              | WITH projections return-tail [WHERE predicate]
match-paths  := match-path (',' match-path)*
match-path   := [ variable '=' ] [selector] [mode] [PATH | PATHS] [GROUP | GROUPS] path
selector     := ANY | ALL | ANY SHORTEST | ALL SHORTEST | SHORTEST positive-int64
mode         := WALK | TRAIL | SIMPLE | ACYCLIC
paths        := path (',' path)*
path         := path-primary ((edge [quantifier] path-primary) | parenthesized | node)*
path-primary := node | parenthesized
parenthesized:= '(' path [WHERE predicate] ')' [quantifier]
quantifier   := '{' integer [',' [integer]] '}' | '{,' [integer] '}' | '+' | '*'
node         := '(' [ variable ] [ label-spec ] [ properties ] [WHERE predicate] ')'
edge         := '-[' filler ']->' | '<-[' filler ']-' | '-[' filler ']-' | '<-[' filler ']->'
              | '->' | '<-' | '-' | '<->'
filler       := [ variable ] [ label-spec ] [cypher-length] [ properties ] [WHERE predicate]
cypher-length:= '*' [integer] ['..' [integer]]
label-spec   := (':' | IS) label-expr | ':' label ( ':' label )+
label-expr   := label-term ('|' label-term)*
label-term   := label-factor ('&' label-factor)*
label-factor := '!' label-primary | label-primary
label-primary:= label | '%' | '(' label-expr ')'
predicate    := and-expr (('OR' | 'XOR') and-expr)*
and-expr     := not-expr ('AND' not-expr)*
not-expr     := NOT not-expr | test
test         := comparison [IS [NOT] (NULL | TRUE | FALSE | UNKNOWN)]
comparison   := concat [comparison-operator concat
                     | IN concat
                     | (STARTS WITH | ENDS WITH | CONTAINS) concat]
concat       := sum ('||' sum)*
sum          := product (('+' | '-') product)*
product      := signed (('*' | '/' | '%') signed)*
signed       := ('+' | '-') signed | operand
labeled      := variable (IS [NOT] LABELED | ':') label-expr
operand      := primary ( '[' predicate ']' | '[' [predicate] '..' [predicate] ']' | '.' field )*
primary      := variable ['.' property] | scalar-literal | parameter | labeled | '(' predicate ')'
              | aggregate '(' ([DISTINCT | ALL] predicate | '*') ')'
              | scalar-function '(' [predicate (',' predicate)*] ')'
              | CAST '(' predicate AS cast-target ')' | CURRENT_DATE | CURRENT_TIMESTAMP
              | '[' [predicate (',' predicate)*] ']'
              | '[' variable IN predicate [WHERE predicate] ['|' predicate] ']'
              | REDUCE '(' variable '=' predicate ',' variable IN predicate '|' predicate ')'
              | (ALL | ANY | NONE | SINGLE) '(' variable IN predicate WHERE predicate ')'
              | '{' [field ':' predicate (',' field ':' predicate)*] '}'
              | CASE [predicate] (WHEN predicate THEN predicate)+ [ELSE predicate] END
              | COALESCE '(' predicate ',' predicate (',' predicate)* ')'
              | NULLIF '(' predicate ',' predicate ')'
parameter    := ('$' | '@') (identifier | ASCII-digits)
properties   := '{' [property ':' predicate (',' property ':' predicate)*] '}'
set-item     := variable '.' property '=' predicate | variable ('=' | '+=') properties
              | variable (IS label | ':' label (':' label)*)
remove-item  := variable '.' property | variable (IS label | ':' label (':' label)*)
projections  := [DISTINCT | ALL] ('*' | projection (',' projection)*)
projection   := predicate [AS alias]
return-tail  := [GROUP BY ('(' ')' | predicate (',' predicate)*)] [ORDER BY sort-item (',' sort-item)*] [paging]
aggregate    := COUNT | SUM | AVG | MIN | MAX | COLLECT_LIST | COLLECT
sort-item    := predicate [ASC | ASCENDING | DESC | DESCENDING] [NULLS (FIRST | LAST)]
paging       := (OFFSET | SKIP) page-value [LIMIT page-value]
              | LIMIT page-value
page-value   := unsigned-int64 | parameter
catalog      := SHOW (LABELS | RELATIONSHIP TYPES | PROPERTY KEYS | INDEXES | OBJECT OWNERSHIP)
```

The repeated-colon form of `label-spec` applies to node patterns only; a relationship has one type.
`LABELED` is positional, like the path mode words: only after `IS` or `IS NOT` in a predicate does
it introduce a label expression, so it remains usable as a name. `!` negates one primary, as ISO
writes it, so `!!A` is `CG00002` and `!(!A)` parses. Each `(...)*` repetition in `label-spec`,
`label-expr`, `label-term` builds one n-ary label node; Boolean runs build n-ary logical nodes. Groups, signs, NOT, CASE, subscripts and collection constructors recurse with stack checks.
A truncated list, including a final [ before a trailing comment, reports CG00002 at EOF.
The pattern-comprehension probe requires a relationship before the bar and stops at the first
item comma; label disjunctions in later list items remain ordinary values. Map key separators
do not start label scans, so unsupported map values retain their CK00001 diagnostics.

A linear statement may start with MATCH, OPTIONAL MATCH, INSERT/CREATE, LET, FILTER, FOR/UNWIND, WITH or RETURN. Each read segment requires RETURN; a modifying segment may omit it. RETURN is terminal within its segment, and NEXT must have a following segment. WITH requires a following stage. Deletion requires an entity binding.
Standalone RETURN evaluates against one empty binding, so `RETURN 1 + 1 AS two` returns one row.
Named assignment is a MATCH-only construct: `INSERT p = (...)` and `CREATE p = (...)` are
unsupported. `MATCH p = (a)-[r:TYPE]->(b) RETURN p` captures the entire matched trail, including
the order of nodes and relationships when an indexed anchor starts in the middle or a relationship
is traversed in reverse. A path variable cannot be deleted or used as a scalar property owner;
the planner rejects `DELETE p` and `RETURN p.name` rather than treating a path as an entity.
A named path cannot rebind an existing variable, including a node or relationship binding.
The sum of path repetition bounds is limited to 64 relationships (`CG00005`). Label expressions and predicates have no
length or nesting limit: a chain of one operator is one n-ary node of any length, and only the stack
bounds parentheses and negations, as in Neo4j (see "Chain length and nesting" below). Match execution
also imposes a materialized-binding limit, documented in the Graph engine design. Quantified paths
enumerate bounded or unbounded repetitions with a runtime 64-hop backstop. The executor's
default TRAIL rule permits repeated nodes but forbids repeated relationship identities within one path; explicit modes follow #1733 below.

### Path modes (#1733)

`MATCH [p =] <mode> [PATH | PATHS] <path>` accepts WALK, TRAIL, SIMPLE and ACYCLIC,
case-insensitively. Modes stay contextual identifiers outside that prefix. Each comma-separated
path has its own mode. `GqlPathPattern.Mode` is nullable: null preserves implicit TRAIL and an
explicit enum value records the written mode. Inner paths retain null, so a hand-built explicit
TRAIL inside a group is distinguishable from an omitted mode. INSERT/CREATE cannot carry modes.

| Mode | Identity rule over the complete path |
| --- | --- |
| WALK | Nodes and relationships may repeat; unbounded quantifiers require a selector and its search budgets |
| TRAIL (default) | Relationship identities cannot repeat; nodes may repeat |
| SIMPLE | Nodes cannot repeat except that the final node may equal the first, closing the path once |
| ACYCLIC | Node identities cannot repeat |

SIMPLE permits a self-loop and a two-hop out-and-back over the same relationship: it imposes
node uniqueness, not an additional TRAIL rule. No hop may extend a closed SIMPLE path.
All modes admit zero-length paths. Identity rules span fixed edges, parenthesized groups,
quantified repetitions and both sides of an interior anchor. They reset for another path.
The parser refuses an explicit nested mode or selector and unselected unbounded WALK with CK00001,
including nested unbounded segments and statements with LIMIT. Selected unbounded WALK executes within the search budgets below.
Malformed text retains CG00002/CG00004/CG00005; undefined enum values and insertion modes in
direct ASTs use CG05001. Shape validation independently refuses nested modes and unselected unbounded WALK.
The 64-hop, physical-work, memory and cancellation contracts remain in force.

Owner decisions 2026-10-25 require all four modes, including SIMPLE beyond Neo4j's grammar.
Local Neo4j references, relative to `C:/Source/repos/neo4j/neo4j` (behavior consulted, no code copied):
`community/cypher/front-end/parser/v25/parser/src/main/antlr4/org/neo4j/cypher/internal/parser/v25/Cypher25Parser.g4:374,401-425`
places a mode after the path assignment and allows PATH/PATHS; its mode set has WALK/TRAIL/ACYCLIC.
`community/cypher/front-end/parser/v25/ast-factory/src/main/scala/org/neo4j/cypher/internal/parser/v25/ast/factory/ExpressionBuilder.scala:226-245`
rejects an explicit mode or selector inside a parenthesized or quantified path.
`community/cypher/front-end/ast/src/main/scala/org/neo4j/cypher/internal/ast/Clause.scala:970-985`
rejects unbounded REPEATABLE ELEMENTS with or without selective search.
`community/cypher/front-end/expressions/src/main/scala/org/neo4j/cypher/internal/expressions/PathMode.scala:44-50`
can make WALK effectively TRAIL under a different match mode. Cohesion has no such implicit
override: WALK always allows repeats. Selector execution preserves this mode contract
and admits unbounded WALK as described below.

### Path selectors: execution (#1734, GQ-SP2/SP3)

MATCH accepts `ANY`, `ALL`, `ANY SHORTEST`, `ALL SHORTEST`, `SHORTEST k [PATHS]`
and `SHORTEST k GROUPS`. The prefix follows an optional `p =` assignment. Its complete
order is selector/count, optional WALK/TRAIL/SIMPLE/ACYCLIC, optional PATH/PATHS, then
GROUP/GROUPS for a counted group selector. For example:
`MATCH p = SHORTEST 2 TRAIL GROUPS ((a)-[r:T WHERE r.w > 0]->*(b) WHERE a.k <> b.k) WHERE path_length(p) > 2 RETURN p`.
GROUP is an accepted singular spelling; canonical rendering uses GROUPS. Counted SHORTEST
requires a positive Int64 literal (CG00011 otherwise); parameter counts, omitted counts and
counted ANY remain outside this delivery (counted ANY retains CK00001).

`GqlPathPattern.Selector` records the kind, count and complete prefix span. Its sealed
`GqlPathSelector` renders a canonical selector independently of optional mode/path tokens.
`GqlPathPattern.ToString()` uses the language renderer to emit the complete semantic path,
including predicates and quantifiers, in selector/mode/PATH/GROUPS order. Round-trip tests
compare every semantic AST field, normalizing source locations and redundant flat elements.
Omission preserves exhaustive matching; an explicit ALL retains its own selection plan.
Selection partitions by the actual ordered start/end identities for each incoming binding,
even for anonymous endpoints, equal endpoints and an interior scan anchor. ANY selects one
admissible path; ALL selects every admissible path. ANY SHORTEST selects one minimum-length
path; counted SHORTEST selects up to k paths in ascending hop count. ALL SHORTEST is equivalent
to SHORTEST 1 GROUPS: groups count distinct hop lengths and retain every path at each selected
length. A path count can split ties; a length-group count cannot. Shortest ties use the
lexicographically ascending complete relationship-identity sequence from start to end, as in
SP1; a node-identity sequence breaks any remaining identity tie. ANY uses that stable identity
order without length ranking. The omitted mode is explicitly TRAIL, Cohesion's existing
choice; SIMPLE, ACYCLIC and WALK retain their documented identity rules.

Labels, relationship directions/types, property maps and inline node/relationship WHERE are
element prefilters. A WHERE inside a parenthesized path is a path prefilter. These remain
inside search, including under quantifiers; each repeated body's variables denote its local
singleton occurrence inside its predicates and group lists outside it. Unquantified group
flattening retains predicate boundary offsets. One iterative pass appends each filter once to
its owning search shape; unquantified groups do not merge accumulated lists. Minimum-round
allocation regressions prove linear growth for nested filters and for a filtered singleton
preceding each growing nested group. Empty filter storage and quantified predicate locals
are allocated lazily; Release planning checks retain the base budgets for node, edge and
quantified paths without selectors or prefilters. Ordinary MATCH WHERE remains a post-selection
filter, including in OPTIONAL MATCH before null extension. It cannot be pushed into an index
anchor or a matches-nothing shortcut for a clause containing a selector. FILTER and later
WITH predicates retain their ordinary downstream placement.
Search prefilters, with or without an explicit selector, may reference their own path bindings, including quantified
singletons and the named path, plus the scope entering MATCH. Peer-path-only dependencies
are refused with CG01016 at the reference, regardless of comma order (Neo4j
`community/cypher/front-end/ast/src/main/scala/org/neo4j/cypher/internal/ast/Clause.scala:1053-1098`).

SP2/SP3 deliver parsing, binding, execution and nonexecuting EXPLAIN, including zero-hop paths, cycles,
bound endpoints and quantified bodies in all modes. A selector admits unbounded WALK into
execution; an unselected unbounded WALK keeps its existing CK00001. EXPLAIN exposes SelectPaths,
PathSearch, ElementPrefilter, PathPrefilter and PostFilter, the count unit, endpoint partition,
resolved mode and stable order. Search work uses CG05004 and retained history/state plus output
use CG05020 under the existing configurable statement ceilings (defaults: 1,000,000 work units,
256 MiB); these are separate from SP1's vertex-visited BFS state contract. GQL's 64-hop backstop
is retained. No claim of memory independent of path histories or output is made.

SP3 executes selectors and inline WHERE in scalar and path requests, including source-free
ASTs, empty input and LIMIT 0. Selected searches anchor at the source boundary and run per
ordered endpoint pair and incoming row. A budgeted finite topology pass, using a safe union of the pattern's directions/types, excludes
disconnected pairs; it never substitutes vertex-visited state for accepting path histories.
ANY SHORTEST and counted SHORTEST use reverse breadth-first distances for element-local
searches (types, directions, node/edge properties and predicates, plus incoming/end bindings).
The filtered transition graph retains vertices and edges, rather than predecessor histories.
A homogeneous repeated hop has exact distances; unions of position-specific steps provide a
safe lower bound, with the matcher enforcing the original pattern. On the shortest-path DAG,
ordered DFS lazily chooses the smallest relationship identity, then node identity, that can
still reach the target in the remaining hops. A shortest path between distinct vertices has
no repeated node, so every mode admits it. No accepting tie buffer is built for a path quota.
Fixed chains and a single fixed-body repetition have unique decompositions. Adjacent quantifiers
instead enumerate physical identities in that same order and validate only the first accepting
source-ordered decomposition, preserving group bindings without counting duplicates.

If k exceeds the number of shortest paths, exact-length reachability layers prune subsequent
lengths and output stops at k. Lengths beyond the graph distance, including unsatisfied pattern
or quantifier minimums, retain the accounted matcher history for TRAIL, SIMPLE and ACYCLIC;
vertex reachability alone cannot prove their admissibility. Graph state costs O(V+E), and layers
for longer lengths cost O(L*V) retained state and O(L*E) work, with L at most 64, in addition to
the mode history and selected output. Path predicates and correlations outside the current
hop/endpoints retain iterative deepening and identity sorting. ALL SHORTEST and counted length
groups also retain exhaustive tie handling: combinatorial output can still refuse cleanly.
Directed n-by-n grid regressions bound one witness by 5E+128 work units and three witnesses
at n=20 by 6E+128 (E=2*n*(n-1)); ALL SHORTEST on that grid still refuses with CG05004.
The independent bounded oracle covers 3,240 selector/mode/pattern/graph cases, checking both
bags and order, including longer paths, cycles, filters, bound endpoints and decompositions.
ALL enumerates the pattern's
complete bounded domain. ANY deepens until its smallest complete identity sequence cannot be
preceded by any truncated prefix. The 64-hop backstop refuses an unresolved search with CG05004;
in particular, an unbounded WALK can have no lexicographic minimum (ever-longer lower-identity
cycles), and is refused rather than returning an unstable ANY witness. ALL WALK likewise refuses
if more qualifying paths exceed the backstop. A completed shortest quota does not explore longer
paths. Repeated prefixes, connectivity visits, retained history states, comparison work and output
all charge the statement's existing work/memory grants; no partial selection survives exhaustion.
Repeated-body predicates run with singleton bindings before group-list publication. A predicate
waiting for the enclosing named path retains that iteration's local bindings until completion.
Unquantified group filters retain their own variables and test the complete candidate before
selection. Multiple quantifier decompositions of one relationship/node identity sequence count
as one physical path; the first source-ordered decomposition supplies its group bindings.

EXPLAIN remains nonexecuting and reports source anchoring, count units and configured ceilings.
The PathSearch description with a live execution budget also reports statement-wide pairs, states, candidates
and selected paths. Existing statement metrics include all physical and viable work and peak
retained bytes. No EventSource, public interface, storage or wire format changes are introduced.
PATH SELECTOR joins the executable language profile, backed by GqlProfileExecutionTests and
GqlPathSelectorExecutionTests; InMemory/TCP GraphPathSelectorWireTests and the Graph NativeAOT
sample exercise the same operators.

Conformance lives in GqlPathSelectorParserTests and the keyword-disposition corpus,
GqlPathSelectorPlanningTests, and GraphPathSelectorWireTests (InMemory and TCP). Former parser
refusal cases now have AST/planning evidence; counted ANY and nested selectors stay pinned.
Local Neo4j behavior consulted, no code copied (paths relative to `C:/Source/repos/neo4j/neo4j`):
`community/cypher/cypher-logical-plans/src/main/scala/org/neo4j/cypher/internal/logical/plans/LogicalPlan.scala:3533-3560`
defines endpoint-pair selectors and distinguishes path counts from length groups;
`community/cypher/ir/src/main/scala/org/neo4j/cypher/internal/ir/NodeConnectionAndPathPattern.scala:389-407`
keeps prefilters in the selective path pattern;
`community/cypher/front-end/parser/v25/parser/src/main/antlr4/org/neo4j/cypher/internal/parser/v25/Cypher25Parser.g4:401-425,431-442,482-485`
provides prefix order and inline predicates. Cohesion requires explicit literal k, retains
TRAIL by default and strengthens arbitrary ties to the identity order above.
`community/cypher/runtime-util/src/main/java/org/neo4j/internal/kernel/api/helpers/traversal/BiDirectionalBFS.java:39-63,78-95`
separates filtered BFS exploration from lazy/eager path retracing; Cohesion uses reverse distances
and the stricter identity-minimal witness contract, without copying reference code.

### Quantified paths (#1144)

`GqlRelationshipPattern.Quantifier` and `GqlQuantifier(Min, Max)` retain inclusive bounds;
null Max means unbounded. `GqlPathPattern.Elements` holds source-ordered `GqlPathNode`,
`GqlPathRelationship` and `GqlParenthesizedPathPattern` elements. The latter carries an enclosed
path and an optional quantifier. Nodes/Relationships remain populated for a path without any
parenthesized element, and are empty for a path with one. A hand-built path may omit Elements
and use the chain lists alone; contradictory representations are CG05001. There is no
catalog, storage or wire format change.

| Spelling | Inclusive bounds |
| --- | --- |
| `->{m}`, `->{m,n}`, `->{m,}`, `->{,n}`, `->{,}` | m..m, m..n, m..unbounded, 0..n, 0..unbounded |
| `->+`, `->*` | 1..unbounded, 0..unbounded |
| `-[r:T*]->`, `-[*..n]->` | 1..unbounded, 1..n |
| `-[*m]->`, `-[*m..n]->`, `-[*m..]->` | m..m, m..n, m..unbounded |
| `-[*0]->`, `-[*0..n]->` | 0..0, 0..n |

The Cypher bound sits after the label expression and before the property map. ISO `{0}` and
`{0,0}` are CG00002; the Cypher zero-length spelling `[*0]` is accepted. Reversed bounds are CG00002, and a bound that cannot fit Int32 is CG00004. A fixed edge contributes 1
to the path sum; a bounded segment contributes its upper bound times its body's edge count;
an unbounded segment contributes its lower bound times that count. A text sum above 64 is
CG00005, including an unbounded lower bound above 64. Hand-built invalid bounds use CG05001.
At runtime a TRAIL segment continues until no unused relationship qualifies; SIMPLE and ACYCLIC apply their node-identity rules; needing a
65th relationship after direction, type, bound identity and the hop's own property checks is
CG05004. Endpoint predicates and projection cannot suppress exhaustion already encountered. An
unordered read LIMIT may stop expansion before a later hop or work limit is reached. Retained
state uses the statement memory budget in place of the former 100,000-binding guard; physical
work still defaults to 1,000,000 selected candidates, with no partial result output on refusal.

`(a) ((x)-[r:T]->(y)){2} (b)` joins the iterations at the same node: each y is the next x,
and both endpoint constraints hold at the junction. Every repeated property map is evaluated
for every occurrence, including deferred references to later outer bindings or comma-separated
paths. A zero repetition tests both outer endpoints on one node, emits a one-node path, and
binds empty group lists. Unknown names inside a Min=0 segment still warn but cannot eliminate
its zero branch. A standalone nullable parenthesized path also anchors on one graph node;
accepting that form departs from Neo4j's rejection of patterns that may have no node pattern.

Variables declared inside a repeated segment are lists of their singleton kind. RETURN * and
WITH * include these group variables in declaration order. Lists preserve source order when
expanded from either end; FOR/UNWIND retain the node/relationship kind, including across WITH
and NEXT. Ordinary list expressions and aggregates can consume them in process. Group provenance
survives CASE, COALESCE, NULLIF, aggregates and container construction/access at the corresponding
depth. Direct member access (`r.w`), DELETE of a group, and reuse of a name as both group and singleton are CG03003.
Property maps inside a repeated segment cannot reference group variables (CG03003); no
implicit choice between an iteration's singleton and the final list is made. A non-negotiated
possible composite final projection is statically CG07016. The existing Execute prohibition
on entity-valued results still applies to a negotiated possibly nonempty group-list projection,
as for collect(entity), with CG05001 naming lists of nodes or relationships. A Max=0 group is
provably empty and projects as `[]`; scalar projections after FOR/UNWIND use the existing wire codec. Named full paths use ExecutePaths.

INSERT/CREATE quantifiers and node quantifiers are CG00002 before writes. Questioned paths
after an edge or parenthesized path, subpath assignments and nested quantifiers each produce
one named CK00001. Inline WHERE executes as a search prefilter above. Elsewhere `?` retains the stray-character
or unsupported-parameter diagnostic. Path selectors parse, bind, execute and EXPLAIN; explicit outer modes execute (#1733). A `--` touching a subpath's `)` or quantifier's `}`, `+` or `*` is CG00008, just as
after a node or edge filler, including the bracketed Cypher star and its bounds. A nested `?`
retains CK00001; path modes inside subpaths receive the same named capability diagnostic. Recursive
subpath parsing uses the CG00009 stack guard. Hand-built subpath nesting beyond 128 levels, cycles and repeated path instances in one walk are refused
iteratively with CG05001 before shape traversal. Parsed text has no nesting cap beyond CG00009.

Bare unbounded quantifiers use implicit trail semantics without ISO's required restrictive mode
or selector; this is a deliberate ISO deviation for the Cypher alias. Relationship uniqueness is
per path; separate comma-separated paths may reuse relationships, differing from Neo4j's clause-wide
default. Mixing bracketed Cypher lengths and ISO quantified subpaths in a clause is accepted.

Behavior consulted, no code copied, relative to `C:/Source/repos/neo4j/neo4j`:
`community/cypher/front-end/expressions/src/main/scala/org/neo4j/cypher/internal/expressions/GraphPatternQuantifier.scala:26-44`
defines plus/star emptiness and interval lower-default zero;
`community/cypher/front-end/parser/v25/parser/src/main/antlr4/org/neo4j/cypher/internal/parser/v25/Cypher25Parser.g4:381-386`
permits both interval bounds to be omitted (`{,}`);
`community/cypher/ir/src/main/scala/org/neo4j/cypher/internal/ir/converters/SimplePatternConverters.scala:108-126`
lowers legacy lengths with lower-default one; and
`community/cypher/front-end/ast/src/main/scala/org/neo4j/cypher/internal/ast/semantics/SemanticPatternCheck.scala:609-619`
declares group variables as lists of their singleton types.

GqlQuantifiedPathParserTests, GqlQuantifiedPathTests, the clause conformance corpus,
GraphQuantifiedPathWireTests and GraphServerProtocolTests pin this surface.

Matching clauses execute in source order; WHERE sees variables bound by its own or earlier clauses,
never later clauses (CG05001). An optional WHERE filters candidate matches before null extension;
comma-separated paths within that clause succeed together. One empty binding precedes the first
clause, so an unmatched leading optional yields one row. New variables have explicit NULL entries,
including named paths; existing bindings are retained. A NULL-bound variable in a later pattern
matches nothing without a scan, property access is NULL, and labeled predicates are UNKNOWN.
DELETE of NULL writes nothing and is not counted; INSERT with a NULL endpoint is CG03003.
Trail uniqueness resets per path, including across clauses. ExecutePaths rejects an optional-only
projected variable at plan time (CG05001), independently of the data; a mandatory-bound variable
is safe. Scalar NULLs use the existing wire encoding.

ISO 14.4's `OPTIONAL { ... }` and `OPTIONAL ( ... )` block operands remain recognized outside T1.
Their one CK00001 names OPTIONAL BLOCK OPERAND, including after WHERE. MANDATORY MATCH remains
unsupported. MATCH after INSERT or DELETE is a supported stage (#1145).
Behavior consulted, no code copied, relative to `C:/Source/repos/neo4j/neo4j`:
`community/cypher/interpreted-runtime/src/main/scala/org/neo4j/cypher/internal/runtime/interpreted/pipes/OptionalExpandAllPipe.scala:54-68`
keeps a row with nulls for an empty expansion or null source; `:174` filters matches to TRUE before
that fallback. `OptionalPipe.scala:30-42` constructs explicit null entries and one row on empty input.
`GqlOptionalMatchParserTests`, the keyword corpus, `GqlProfileExecutionTests`, `GqlOptionalMatchTests`
and `GraphOptionalMatchWireTests` pin these semantics and rejected forms.

### Dedicated catalog statement choice (C2)

The separate `SHOW` grammar exposes metadata through the query language without constructing fake
nodes or relationships for `MATCH`. Catalog definitions have stable catalog identities but are not
graph elements. The extension adds no server scope or database selector and changes no public
interface. Keywords are case-insensitive and comments and one optional trailing semicolon follow
the ordinary lexer rules. A statement cannot combine `SHOW` with graph matching, projection or
mutation. Attempted mutation composition produces `CG00007`, including verbs such as `SET` and
`DROP`. SET executes only as a graph stage. The engine validates direct AST requests too.

The [engine's catalog section](../../Assimalign.Cohesion.Database.Graph/docs/DESIGN.md#catalog-introspection-c2)
defines ordered result columns, SQL-consistent ownership vocabulary and session snapshot behavior.
The parser describes only the metadata subject; it never accesses or caches catalog state.

## Composite queries: UNION, EXCEPT, INTERSECT and OTHERWISE (#1737, GQ-Q1/Q2)

A query may combine complete read-only operands with UNION, EXCEPT, INTERSECT or OTHERWISE. All
have the same precedence and associate left; parentheses explicitly group operands (a Cohesion
extension). NEXT has lower precedence: each of its segments is a complete composite expression.
A NEXT inside a parenthesized operand remains in that operand. WITH stays operand-local. The immutable
`GqlCompositeQuery(Left, Operator, Right)` tree retains that grouping; `GqlQueryExpression.Composite`
is null for linear statements. A composite inside a NEXT pipeline is represented by the sealed
`GqlCompositeStage`, with ordinary stages and GqlNextStage preserving source order. Rendering uses the existing explicit-stack path/scalar renderer,
including read stages and per-operand projections. Unaliased parsed projections retain their
normalized expression text so canonical quoting cannot rename a result column.
The stage/body reference is shared with the composite body so ordinary AST object size is unchanged.
Capability scanning recursively visits query groups with fresh query context and relative nesting
depth. Pattern groups, scalar parentheses and EXISTS bodies keep their separate contexts.

Every operand must be read-only and return the same ordinal, case-sensitive column names in the
same order. Proven incompatible value kinds (including list/map and entity kinds) fail before
execution; all numeric kinds are compatible, NULL imposes no kind, and unknown property kinds
are checked when rows arrive. Runtime incompatible kinds also fail without publishing partial
results. Collection contents retain the existing heterogeneous structural-value contract; proven
incompatible nested entity kinds are refused. Mixing UNION and UNION ALL anywhere in one tree,
including a parenthesized group, is refused. Bare UNION and explicit UNION DISTINCT both mean
distinct and may mix; rendering normalizes UNION DISTINCT to UNION. EXCEPT and INTERSECT also
default to DISTINCT, normalized to the bare operator, or accept ALL. Quantifiers are local and may
mix across EXCEPT/INTERSECT; otherwise mixed conjunctions are admitted. The UNION-mode refusal
remains a Cohesion restriction. EXCEPT, INTERSECT and OTHERWISE remain contextual identifiers in
scalar/binding slots. OTHERWISE accepts no quantifier.

EXCEPT DISTINCT returns one representative only when absent on the right; INTERSECT DISTINCT
returns one when present on both sides. EXCEPT ALL returns max(m-n,0) copies and INTERSECT ALL
min(m,n), using the existing structural row comparer. Execution retains eligible left occurrences
in their original order and representation. OTHERWISE returns the whole left result if nonempty,
including NULL rows and duplicates, and opens the right cursor only on zero left rows. Left errors
propagate. This lazy whole-result contract is Cohesion-specific, with no Neo4j precedent; the right
operand still participates in all static validation, metadata reconciliation and wire admission.

ORDER BY, OFFSET/SKIP and LIMIT belong to each operand's RETURN. A tail after the final RETURN
therefore affects only the last operand. There is no global composite ordering/page clause;
a suffix after a parenthesized composite is CK00001. Output visits operands in source order;
UNION keeps the first occurrence, and UNION ALL preserves all occurrences, although applications
must use ORDER BY when ordering is part of their query contract. Column names come from the first
operand; numeric metadata widens across operands, NULL is neutral, and incompatible or dynamic
values use the existing DatabaseType.Null (unknown). Actual values retain their CLR types.
EXPLAIN plans every operand and preserves the binary grouping in operator-named nodes; NEXT
pipelines place those groups alongside ordered Next and subsequent stage nodes.

Local Neo4j reference (no code copied), relative to `C:/Source/repos/neo4j/neo4j`:
`community/cypher/front-end/ast/src/main/scala/org/neo4j/cypher/internal/ast/semantics/SemanticError.scala:735-737,755-763`
supplies matching-column and mixed-mode refusals; the consulted v25 parser is
`community/cypher/front-end/parser/v25/parser/src/main/antlr4/org/neo4j/cypher/internal/parser/v25/Cypher25Parser.g4:75-77,138-140,166-176`.
NEXT precedence follows Neo4j's parser test
`community/cypher/front-end/parser/parser-test/src/test/scala/org/neo4j/cypher/internal/ast/factory/query/NextStatementParserTest.scala:350-378`
(P12) and `Cypher25Parser.g4:67-77`; line 76 also admits explicit DISTINCT.
The independently maintained [TuGraph GQL grammar](https://github.com/TuGraph-family/gql-grammar/blob/main/GQLParser.g4#L665-L713)
uses a left-recursive composite expression, and its
[primitive result rule](https://github.com/TuGraph-family/gql-grammar/blob/main/GQLParser.g4#L790-L805)
attaches ordering/paging to RETURN. These are grammar references, not an ISO conformance claim
for Cohesion's parentheses extension.

`GqlCompositeQueryParserTests`, the keyword disposition corpus and the live profile corpus
cover every operator and quantifier, equal precedence, grouping, NEXT and capability scanning.
The [OpenGQL grammar, 14.2](https://github.com/opengql/grammar/blob/main/GQL.g4#L479-L498)
has one left-recursive conjunction level, including OTHERWISE and independently optional set
quantifiers; it has no INTERSECT precedence tier or syntactic prohibition on mixing. This is
grammar evidence; no stronger ISO semantic claim is inferred from it. Neo4j
Cypher25Parser.g4:75-77 supplies only UNION and is not fallback precedent.
Diagnostics: CG00015 missing operand,
CG00016 mixed UNION modes, CG01020 column names/order/count mismatch, CG02017 incompatible
result shape, CG05029 modifying operand, CG05030 UNION distinct-state memory refusal, and
CG05032 EXCEPT/INTERSECT retained-state memory refusal, including empty execution frames.
Every parent hash visit or forwarded row in a mixed tree consumes the shared work budget (CG05004).
No new interface or wire/disk format.

NEXT passes each variable's proven binding kind as well as its value shape into every operand;
named paths remain valid for ExecutePaths and path property access remains a planning CG03003,
even on empty inputs. Union boundaries use decoded variable names for later references and star.
Runtime type and nullability reconciliation also applies to a union ending a NEXT chain.
Standalone ORDER BY/OFFSET/SKIP/LIMIT after NEXT retains CK00001 naming the unsupported form.
INSERT followed by NEXT RETURN * parses, but its empty final projection is CG05001 during planning,
before acquiring the writer lock or applying any mutation.

## Linear composition (#1145)

The stage list is the planner/executor input. A leading NEXT or WITH immediately followed by NEXT is CG00002. A hand-built RETURN predicate is CG05001; predicates belong to WITH. The original constructors retain the owner-selected single-segment facade for hand-built ASTs; SHOW composition is rejected through either shape. Empty or missing mandatory stage operands are refused by the planner with CG05001. NEXT and FOR are profile keywords and also remain legal variable names and aliases, so `RETURN a AS next NEXT ...` and property keys named next or for remain valid.

LET adds new bindings, evaluating comma-separated assignments in order. Value variables may hold maps, lists or entities; member access admits maps/entities, preserving the accessed value shape. A known scalar owner is CG03003 during planning; an invalid runtime container is CG02013. FILTER (also FILTER WHERE) keeps only TRUE. FOR and Cypher UNWIND expand a list per incoming row; NULL gives zero rows. A non-list, including string or byte[], is CG02013. The ISO WITH ORDINALITY and WITH OFFSET suffixes are excluded with one CK00001 naming FOR ORDINALITY/OFFSET SUFFIX. FOREACH, CALL and NEXT YIELD remain capability refusals; UNION is a composite-query conjunction. Standalone ORDER BY/OFFSET/SKIP/LIMIT stages, including after LET, FILTER, FOR or writes, remain CK00001 under the owner default.

RETURN followed by NEXT and WITH retain only aliased columns and bare-variable projections. Carried names are decoded variable names, including under star; displayed quotes and grouping do not create different bindings. Duplicate carried bindings are CG01015 during planning, including EXPLAIN. Star expands all current bindings in scope order, including LET/FOR values and the preceding projection's order. Unaliased computed columns are accepted but cannot be referenced in a later stage; this is an explicit superset divergence from Cypher. WITH DISTINCT uses the same exact numeric tuple comparer as RETURN DISTINCT. Entity kinds, optionality, possible composite values and Int64 COUNT types survive boundaries. An unbound variable is CG05001; LET/FOR rebinding, a scalar used as an entity, or a later reference to a deleted variable is CG03003. A NEXT after a modifying segment without RETURN starts with one empty binding and no scope.

WITH's ORDER BY and WHERE see both projected aliases and the incoming scope when neither DISTINCT nor grouping is present. Aliases shadow incoming names. With DISTINCT or grouping, only projected/grouped values remain visible. WHERE applies after ORDER BY, OFFSET and LIMIT. Each projection has its own grouping and ordering state, so one stage's aliases never validate a later stage. Function detection applies to every new value position, including row-evaluated pattern maps. Pattern maps evaluate scalar expressions against each row; list/map syntax keeps its graph-storage CK00001 and composite values remain CG03003.

Read stages stream borrowed slot bindings in source order; retention acquires explicit value ownership. Mutation target tables materialize before applying their write stage. Unknown labels empty their MATCH stage only, and names introduced by an earlier INSERT are treated as available. A statement sees its own writes under Snapshot and ReadCommitted. A write anywhere takes the writer lock before the first stage. FOR elements and selected MATCH candidates share the physical-work budget; retained tables/results share the memory budget (#1533). Only the final columns determine Execute admission; a scalar LET/FOR variable is scalar, possible composites require negotiation, and carried entities still require ExecutePaths. ExecutePaths accepts a read-only composed statement with one mandatory entity/path final column and rejects any write stage.

Behavior consulted, no code copied, relative to `C:/Source/repos/neo4j/neo4j`:
`community/cypher/front-end/ast/src/main/scala/org/neo4j/cypher/internal/ast/Clause.scala:1965-1970` defines WITH's incoming scope;
`community/cypher/cypher-planner/src/main/scala/org/neo4j/cypher/internal/compiler/ast/convert/plannerQuery/ClauseConverters.scala:1018-1032` puts selection on the paged projection horizon;
`community/cypher/runtime-util/src/main/scala/org/neo4j/cypher/internal/runtime/ListSupport.scala:56-67` wraps a scalar as one row. Cohesion deliberately rejects that scalar UNWIND behavior with CG02013; NULL still yields zero rows.
`community/cypher/interpreted-runtime/src/main/scala/org/neo4j/cypher/internal/runtime/interpreted/pipes/LazyLabel.scala:32-37` retries unresolved label tokens, motivating stage-aware insertion name resolution.

GqlLinearCompositionParserTests, GqlKeywordDispositionTests, GqlProfileExecutionTests, GqlLinearCompositionTests, GraphConcurrencyTests and GraphLinearCompositionWireTests cover the grammar, scope, execution, locking and both wire transports. No public interface, catalog format or protocol frame is added. Typed stage dispatch preserves trimming and NativeAOT compatibility.

## EXPLAIN (#1104)

ISO/IEC 39075 defines no EXPLAIN. This is a Cohesion extension on the same footing as SHOW,
not a D3 Cypher alias. The parser recognizes the prefix before SHOW dispatch, marks the parsed
`GqlQueryExpression.IsExplain`, and retains normal statement completeness. EXPLAIN SHOW,
transaction control, nested EXPLAIN, EXPLAIN ANALYZE and PROFILE receive `CK00001`.
`EXPLAIN` is contextual only at the statement prefix, matching PostgreSQL's unreserved keyword
classification (`src/include/parser/kwlist.h:167` in the local clone). It is not a lexer keyword;
unquoted `explain` remains a variable, label, relationship type, property or projection alias.
The keyword-disposition corpus covers these identifiers both with and without the prefix.

The engine returns the shared
[description columns](../../Assimalign.Cohesion.Database.Execution/docs/DESIGN.md#plan-descriptions-1104)
on Execute, including `EXPLAIN MATCH (a:Person) RETURN a`. ExecutePaths rejects it with
`CG05001` and leaves the server session ready. The Graph engine owns anchor/expansion names.

WITH WHERE has its own validation scope: DISTINCT/aggregation expose only projected names; other WITH predicates also see incoming names. Aggregate expressions in WHERE are CG05001 even if repeated from the projection; their aliases remain available. ORDER BY retains its separate projected-expression reuse rules.

MATCH map entries are predicates on the bound pattern, including self references and references to later elements of the same path. Evaluation waits for every referenced variable regardless of anchor/expansion direction. INSERT/CREATE maps reference only variables from earlier stages; same-clause references are CG05001 during planning.

## RETURN expressions and duplicate elimination (#1141)

**Breaking AST change:** `GqlProjection(GqlExpression Expression, string? Alias)` replaces the
variable/property tuple. `GqlVariableExpression` represents a bare binding; property access uses
`GqlPropertyExpression`. Hand-built ASTs construct those nodes explicitly. RETURN uses the same
value-expression parser and evaluator as WHERE, including parameters, arithmetic, concatenation,
null/truth tests and labeled predicates. `GqlQueryExpression.SetQuantifier` defaults to ALL;
`ReturnStar` is separate from the projection list and cannot accompany explicit items.
Bare references enable entity/path equality by identity across materializations and IS NULL tests
in either expression context.

An alias names its column exactly. Otherwise the column name is normalized source text: comments
are removed, token spacing is canonical, and identifier/keyword spelling, quoted text and grouping
are preserved. Thus `a . age /* comment */ + 1` names `a.age + 1`. A source-free hand-built
expression receives equivalent rendered syntax, with parentheses around binary expressions.
This implements the owner's naming default as a Cohesion choice. The [published ISO preview](https://webstore.ansi.org/preview-pages/ISO/preview_ISO%2BIEC%2B39075-2024.pdf)
locates 14.11 on page 185 but does not expose its naming rules; the [editors' grammar, 14.11](https://github.com/opengql/grammar/blob/main/GQL.g4#L635-L654)
confirms the optional quantifier, star, expression items and AS alias, without defining implicit
column names. Normalized computed names are therefore not claimed as verified ISO semantics.

RETURN * expands first declarations, in source order, including a path assignment before its
elements, and skips anonymous elements and repeated references. `(a)-[r]->(b)` yields a, r, b.
No named binding (`MATCH () RETURN *` or standalone `RETURN *`) is CG00002. The planner validates
equivalent hand-built shapes with CG05001. Aliases do not bind variables for neighboring items.
Unbound variables/parameters remain CG05001; a path property reference remains CG03003.
Final column names must be unique under ordinal case-sensitive comparison. Duplicate implicit
names, explicit aliases, or collisions between them raise CG01015 at the second expression
before execution, even for empty matches and EXPLAIN. Path-request shape errors retain precedence.
Behavior consulted, no code copied: Neo4j `community/cypher/front-end/frontend/src/main/scala/org/neo4j/cypher/internal/frontend/phases/parserTransformers/scoping/VariableCheckerUtil.scala:434-444`
(relative to C:/Source/repos/neo4j/neo4j) diagnoses the second duplicate result column.

DISTINCT compares the complete projected tuple: NULLs are equal, strings compare ordinally,
numbers compare by exact mathematical value across runtime numeric types, and nodes and
relationships compare by identity. Paths compare their ordered node and relationship identities,
including traversal direction, independently of materialization. Exact reduced numeric fractions
keep hashing consistent without merging adjacent Int64 values above 2^53 or rounding subnormals
to zero. Numeric comparisons (=, <>, !=, <, <=, >, >=), property matching and IN use the same
exact fractions: 0.1m = 0.1d is FALSE, 1 = 1.0 is TRUE, and mixed comparisons above 2^53
retain every integer bit. NULL comparisons remain UNKNOWN, while DISTINCT groups NULLs;
signed zeros compare equal. Overflowing numeric text such as `1e999` fails parsing with CG00004.
Non-finite AST literals and parameters, including NaN, fail planning with CG03003.
Internal value equality groups NaNs across floating tags and payloads with consistent hashes.
This owner choice changes #1140's comparison promotion for reuse by #1145/#1147;
arithmetic still follows the separate promotion table below. Behavior consulted, no code copied,
relative to C:/Source/repos/neo4j/neo4j: `community/values/src/main/java/org/neo4j/values/storable/Value.java:162`
connects non-NaN predicate equality to value equality, `IntegralValue.java:77-95` provides exact
mixed numeric equality/ordering, and `community/cypher/interpreted-runtime/src/main/scala/org/neo4j/cypher/internal/runtime/interpreted/pipes/DistinctPipe.scala:78`
uses value equality for duplicate elimination.
The first representative retains its runtime type; ALL retains every row. These implement the
owner's DISTINCT defaults. Reference behavior consulted, no code copied: local Neo4j
`community/values/src/main/java/org/neo4j/values/virtual/VirtualNodeValue.java:52-61`,
`VirtualRelationshipValue.java:64-72`, and `VirtualPathValue.java:85-98` compare entity IDs and
ordered path identities rather than property maps.

The owner default for Execute is static and conservative: planned result kinds are Scalar, Node,
Relationship, Path and EntityValue (a value that may contain an entity). Only statically scalar columns pass Execute, even on empty matches; the
check precedes reads, writer locking and mutations. Entity columns retain the ExecutePaths error
and ExecutionFailure. EXPLAIN describes the plan and remains scalar even for an entity RETURN.
Collection and CASE result branches propagate possible entities through the closed
kind dispatch, including under list/map constructors and field references (#1146). Path requests accept one bare bound variable, including DISTINCT, or star over
one named binding. Computed, multiple and multi-binding star projections use the existing
CG05001 request-shape diagnostic. GraphPathResult completion diagnostics remain unchanged.

Cypher map projection (`a {.name}`) remains CK00001 naming MAP PROJECTION. NEXT and WITH, including WITH DISTINCT, execute through the linear pipeline. Immediate return-after-delete remains
CK00001. No catalog format or protocol frame changes are involved. `GqlReturnParserTests`,
the keyword-disposition corpus, `GqlProfileExecutionTests`, `GraphReturnTests` and
`GraphReturnWireTests` cover this surface and its boundaries.

## Conditional expressions and composite values (#1146)

CASE, WHEN, THEN, ELSE, END, COALESCE and NULLIF sit in Profile.Keywords; the function
registry independently advertises aggregates and the core scalar set (#1148). The keyword-disposition corpus has a live positive parse for each,
and GqlProfileExecutionTests measures both CASE forms and both abbreviations. CASE is
advertised as one clause. COALESCE and NULLIF parse into case-abbreviation AST nodes,
never registry function calls. The public AST also adds GqlCaseExpression and its ordered
alternatives, GqlMapExpression, GqlSubscriptExpression and GqlMemberExpression. GqlListExpression
now works in every value position; GqlInExpression.List is a GqlExpression (breaking change).

Searched CASE selects the first TRUE predicate. Simple CASE evaluates its operand once
and compares each WHEN operand in source order: WHEN 17, 42 matches either;
WHEN > 30 (and =, <>, !=, <, <=, >=) uses the exact predicate comparison; WHEN IS NULL
and WHEN IS NOT NULL test the operand directly. WHEN NULL never matches, including a NULL operand.
The parser expands a comma list into ordered alternatives sharing the THEN expression;
GqlCaseAlternative.Comparison records the comparison without reevaluating the CASE operand.
Only the selected result branch evaluates; no matching branch and no ELSE yields NULL.
COALESCE evaluates left to right until non-NULL; NULLIF evaluates both operands once and
returns NULL only for TRUE equality, otherwise its first operand. CASE result types are
the union of possible runtime types, without coercion to a common numeric/string type;
the selected runtime type is preserved and column metadata remains DatabaseType.Null.
Planning still validates all referenced bindings and every expression shape in skipped branches.

Lists materialize as object?[]; maps as read-only ordinal IReadOnlyDictionary<string, object?>.
Parameters accept BCL IList implementations with zero-based one-dimensional arrays (including object?[] and List<long>, excluding
byte[] binary scalars) and IReadOnlyDictionary<string, object?> implementations, including
Dictionary<string, object?>. Each referenced value is recursively snapshotted once per
execution; invalid/non-finite leaves and cycles or parameter bounds fail with CG03003
before reads or writes. All values allow depth 64, 65536 total values including the root and
16 MiB encoded bytes. Constructors validate these bounds inside the statement transaction with
CG02013, including in-process execution, so a refused result rolls back its writes. Strings
count real UTF-8 bytes, zero escaping and framing.
INSERT/CREATE and MATCH pattern property maps remain scalar-only (CG03003 for composite
parameters, CK00001 naming storage for list/map syntax); whole-map (n $props) stays CK00001.

Negative list indexes count from the end. Out-of-range indexes, missing map fields and
NULL containers/indexes yield NULL. Lists require integral indexes; maps require string
keys. Node and relationship values also accept string subscripts and member access:
(a).name, COALESCE(b,a).name and a['name'] read scalar stored properties and pass Execute.
Only containers that are neither entities nor maps fail member lookup. Invalid accesses,
including (a.name).x on a string, use #1140's CG02013.
The parser keeps a.b.c on a stored property as CK00001 while {k:{j:1}}.k.j and $m.k
are field access. List comprehensions and slices execute as described below; pattern comprehensions execute through the correlated collection boundary below.
UNWIND/FOR expand literal and parameter lists through the linear pipeline (#1145).

List equality requires equal lengths and element-wise equality. Map equality requires
equal ordinal key sets and value equality, independent of entry order. FALSE dominates
UNKNOWN; otherwise any NULL comparison makes the result UNKNOWN. IN, simple CASE and
NULLIF use that rule with the owner's exact numeric comparison (0.1m differs from 0.1d).
DISTINCT uses the same structure and exact numbers but groups corresponding NULLs, with
matching structural hashes. Ordering comparisons are lexicographic for lists; maps compare
count, sorted ordinal keys, then corresponding values. An encountered NULL or incomparable
value yields UNKNOWN. ORDER BY (#1142) uses total
ordering with the same container rules and NULL after all non-NULL nested values, rather than
using predicate UNKNOWN as an order. For the scalar/composite family the ascending type
order is map, list, string, Boolean, number, NULL; nested sorting uses that total order and
the owner's exact numeric comparisons. Explicit NULLS placement applies to a whole sort key.

Behavior consulted, no code copied, relative to C:/Source/repos/neo4j/neo4j:
community/cypher/interpreted-runtime/src/main/scala/org/neo4j/cypher/internal/runtime/interpreted/commands/expressions/CaseExpression.scala:33-40
selects the first TRUE branch and defaults to NULL;
community/values/src/main/java/org/neo4j/values/SequenceValue.java:120-136 and :187-202
define ternary list equality and lexicographic ordering;
community/values/src/main/java/org/neo4j/values/virtual/MapValue.java:537-595 and :598-634
define map ordering and ternary equality;
community/cypher/runtime-util/src/main/java/org/neo4j/cypher/operations/CypherFunctions.java:3381-3398
implements integral, negative and out-of-range indexing, and :3414-3421 delegates map lookup.
MapValue.java:125-126 returns NO_VALUE for absent keys.
community/cypher/front-end/parser/v25/ast-factory/src/main/scala/org/neo4j/cypher/internal/parser/v25/ast/factory/ExpressionBuilder.scala:532-569
lowers plain operands to Equals, comparison parts to binaryPredicate and null tests to advancedPredicate.
Cypher25Parser.g4:688-707 in community/cypher/front-end/parser/v25/parser/src/main/antlr4/org/neo4j/cypher/internal/parser/v25/
accepts comma operands, comparison parts and IS [NOT] NULL.
ISO GQL 20.7, transcribed by the [openGQL grammar](https://github.com/opengql/grammar/blob/main/GQL.g4#L2192-L2245),
defines whenOperandList, comparisonPredicatePart2 and nullPredicatePart2 respectively.
[Neo4j simple CASE](https://neo4j.com/docs/cypher-manual/current/expressions/conditional-expressions/#simple-case)
also accepts comma operands; its extended simple CASE accepts explicit comparisons and IS [NOT] NULL.
These forms share Cohesion's exact comparison rule (owner, 2026-10-17).
CypherFunctions.java:988-1017 and :1061-1079 in community/cypher/runtime-util/src/main/java/org/neo4j/cypher/operations/
read node/relationship properties through member and string-subscript access.
community/cypher/interpreted-runtime/src/main/scala/org/neo4j/cypher/internal/runtime/interpreted/commands/expressions/ListSlice.scala:34-61
dispatches omitted bounds to slicing operations, implemented by GQ-C1 below.
community/values/src/main/java/org/neo4j/values/AnyValueComparator.java:87-118 and :146-155
separates ternary orderability from total sorting (NULL and mixed groups are unordered in predicates).
Its :44-85 places NULL last and sequences before scalars; virtual/VirtualValueGroup.java:26-32
orders maps before lists, while storable/ValueGroup.java:56-65 orders zoned timestamps, dates, text, Boolean and numbers.
The selected exact numeric rule is the owner
decision, including within these composites.

## Correlated EXISTS patterns (#1537, GQ-E1)

GqlExistsExpression carries a nonempty list of ordinary GqlMatchClause nodes. Both explicit
MATCH blocks (including successive MATCH/OPTIONAL MATCH clauses) and implicit pattern lists
execute; rendering canonicalizes to explicit MATCH. Scalar NOT supplies NOT EXISTS. The
EXISTS profile entry covers these braced patterns only. Empty/unbraced operands use CG00013;
full query bodies, parenthesized EXISTS, COUNT/COLLECT blocks and bare pattern predicates
retain CK00001. Inner RETURN/WITH/NEXT/UNION/CALL/order/page/write stages are excluded.

Outer names correlate by lexical identity, including iterator names and enclosing EXISTS
locals. New names remain inside the braces. Pattern NULL arguments match nothing; EXISTS
always returns a Boolean. Value arguments remain available to maps and predicates; entity
positions enforce their node/relationship/path kinds (CG01019 statically, CG02016 dynamically).
EXISTS is available throughout Boolean and value expressions, including CASE, projection,
ordering, list comprehensions and list predicates. Aggregates inside inner predicates retain
ordinary MATCH's aggregate-placement refusal. No disk or wire format changes are involved.
Hand-built ASTs may reuse the same expression in multiple lexical scopes; each occurrence
binds independently. Iterators shadow enclosing names inside their bodies, including through
nested EXISTS, without adding dependencies on the shadowed binding. Unsupported predicate
vocabulary inside an EXISTS body uses the same context-aware CK00001 checks as outer
predicates, while pattern variables, labels, relationship types and property names remain names.

Behavior consulted, no code copied: C:/Source/repos/neo4j/neo4j/
community/cypher/front-end/parser/v25/parser/src/main/antlr4/org/neo4j/cypher/internal/parser/v25/Cypher25Parser.g4:816-818
admits explicit query and implicit pattern bodies; Cohesion restricts the explicit body to MATCH.
community/cypher/slotted-runtime/src/main/scala/org/neo4j/cypher/internal/runtime/slotted/pipes/SelectOrSemiApplySlottedPipe.scala:46-59
seeds an inner execution from the outer row, tests hasNext (negated for anti apply), closes the
inner iterator immediately, and preserves the outer row.

## List comprehensions and slices (#1736, GQ-C1)

`[x IN list [WHERE predicate] [| projection]]` evaluates its source once in the enclosing
scope, then visits elements in order. The iterator shadows an outer name only in WHERE and
projection; nested iterators have independent lexical scopes. Omitting projection keeps each
accepted element. Only TRUE passes WHERE; FALSE and NULL discard the element and skip projection.
A NULL source yields NULL, and an empty source yields an empty list without evaluating the body.
The parser resolves a top-level label/projection bar by preferring a complete WHERE/projection
parse, then filter-only syntax; parenthesized label unions stay within the filter.
The source must be a list (not a string, map or binary scalar). Function-spelled iterator names
remain legal. Aggregates may supply the source list; an aggregate in WHERE or projection is
CG05024, including under EXPLAIN. All names and parameters validate even for empty/NULL sources.
Known non-Boolean filter types fail planning with CG02013 independently of source cardinality
or NULL; unknown types retain the runtime Boolean/NULL check.

`list[from..to]`, `list[from..]` and `list[..to]` use an inclusive start and
exclusive end. Omitted bounds mean zero/list length. Negative bounds count from the end;
bounds clamp to the list, and a reversed/empty interval yields an empty list. Explicit NULL
bounds or a NULL source yield NULL. Bounds evaluate before the NULL result is selected.
Following Neo4j semantic analysis, planning rejects a slice with both bounds omitted (CG00012)
and known non-integer bound types (CG02014), including integral-valued FLOAT literals.
Dynamic bounds retain Neo4j runtime conversion: finite numeric values truncate toward zero
before signed Int32 range validation; Decimal values use the same rule exactly. Invalid sources,
bound kinds or ranges are CG02014. Non-Boolean filters retain CG02013. Missing/empty iterator
names in hand-built ASTs use CG01017; ordinary missing bindings retain CG05001.

GqlListComprehensionExpression and GqlListSliceExpression preserve optional operands and source
spans. Both render through the path renderer and source-free EXPLAIN expression renderer.
Shape propagation substitutes the source's element shape for the lexical iterator. Scalar
projections such as `[r IN rs | r.w]` cross Execute for quantified-pattern groups and path lists,
including through nested containers, slices and WITH/NEXT. Entity-producing results keep the
existing static Execute refusal until #1662, even when a filter is FALSE or a source is empty.
Composite scalar lists still require graph.composite-values under protocol 2.1.

Every visited comprehension element and copied slice element consumes the same statement work
budget (CG05004). Sources, output buffers and nested results share the retained-memory ledger
(CG05020); buffers reserve capacity before allocation, and failure releases ownership and rolls
back writes. Constructed-value depth/count/byte bounds retain CG02013. Ordinary expressions
keep their existing allocation path. No wire or persisted format changes are needed.

Reduce and ALL/ANY/NONE/SINGLE execute as described below.
[Pattern comprehensions](#pattern-comprehensions-1736-gq-c3) collect correlated matches.
Map comprehensions and allReduce are outside this plan.

Reference evidence (local Neo4j clone, paths relative to its root):

- `community/cypher/front-end/parser/v25/parser/src/main/antlr4/org/neo4j/cypher/internal/parser/v25/Cypher25Parser.g4:714-716`: optional filter/projection grammar; `community/cypher/front-end/parser/parser-test/src/test/scala/org/neo4j/cypher/internal/ast/factory/expression/LabelExpressionsParserTest.scala:741-752,780-824,880-958` fixes the label/projection bar ambiguity.
- `community/cypher/interpreted-runtime/src/main/scala/org/neo4j/cypher/internal/runtime/interpreted/commands/expressions/ExtractFunction.scala:35-52` and `FilterFunction.scala:36-52`: ordered element evaluation, TRUE-only filtering and NULL-in/NULL-out inheritance.
- `community/cypher/front-end/ast/src/main/scala/org/neo4j/cypher/internal/ast/semantics/SemanticExpressionCheck.scala:1099-1117,1265-1285`: source checking, scoped iterator declaration and projected element type.
- `community/cypher/front-end/ast/src/main/scala/org/neo4j/cypher/internal/ast/semantics/SemanticExpressionCheck.scala:726-737` and `community/cypher/front-end/ast-test/src/test/scala/org/neo4j/cypher/internal/ast/semantics/ListSliceTest.scala:51-82`: at least one bound and INTEGER bound types, independently of runtime conversion.
- `community/cypher/interpreted-runtime/src/main/scala/org/neo4j/cypher/internal/runtime/interpreted/commands/expressions/ListSlice.scala:34-66` and `community/cypher/runtime-util/src/main/java/org/neo4j/cypher/operations/CypherFunctions.java:3257-3301,3485-3509,3533-3550`: open bounds, NULL propagation, negative offsets and numeric Int32 conversion.

The parser/conformance corpus, GraphListComprehensionTests, GraphCompositeWireTests and Graph
NativeAOT sample cover grammar, lexical shadowing, null/empty lists, slice forms, groups/path
lists, static admission, EXPLAIN, budget rollback and both transports.

### Pattern comprehensions (#1736, GQ-C3)

`GqlPatternComprehensionExpression(Pattern, Predicate, Projection)` carries a nonempty
relationship chain, optional local path name, optional WHERE and mandatory projection.
`[ [p =] (a)-[r:T]->(b) [WHERE predicate] | projection ]` drains the E1 correlated
subplan in match order. Duplicates and NULL projections are retained. No matches, including
an existing NULL node/relationship argument, produce an empty list. The optional path name
is a new local binding and may shadow an outer name; other existing pattern names correlate.
Names introduced inside do not escape. Parsed and hand-built AST occurrences bind separately.
Fixed chains and existing relationship quantifiers (ISO and Cypher bracketed forms) are accepted.
Lookahead recognizes all MATCH relationship directions, including abbreviated `->`, `<-`, `-`
and touching `<->` followed by `{m,n}`, `{m}`, `{m,}`, `{,n}`, `{,}`, `+` or `*`.
The path parser validates their bounds after the comprehension is recognized. Path selectors,
explicit path modes and parenthesized quantified path groups are outside this expression form.
A relationship quantified to zero still satisfies the syntactically nonempty requirement.

The form is an expression in RETURN, WHERE, SET scalar values, aggregate operands, nested
pattern/list comprehensions, list predicates and reduce. The inner projection may contain
nested collection expressions but cannot contain an aggregate over its own match scope
(CG05028). A known non-Boolean WHERE is CG02013 even on an empty scan. The existing property
storage restrictions still apply to collection-valued SET/INSERT values. Top-level label
bars use the list-comprehension disambiguation rule: prefer a complete WHERE | projection.
Malformed missing-projection forms use CG00014 or the ordinary CG00002 syntax diagnostic.

Lexical shape analysis binds inner node/relationship kinds and leaves outer callbacks live
through projection, including reduce accumulator closure. Scalar projections of inner entities
and groups cross Execute using protocol 2.1
`graph.composite-values`; entity-valued lists retain CG05001 until #1662, including empty
or FALSE-filtered lists. In-process results can contain nodes, relationships and paths.
No public interface, assembly reference, wire capability, format or disk version changes.

Reference evidence (local Neo4j clone; no code copied):

- `community/cypher/front-end/parser/v25/parser/src/main/antlr4/org/neo4j/cypher/internal/parser/v25/Cypher25Parser.g4:723-725`: expression form with required projection and nonempty chain.
- `community/cypher/front-end/frontend/src/main/scala/org/neo4j/cypher/internal/frontend/phases/parserTransformers/ReplacePatternComprehensionWithCollectSubqueryRewriter.scala:59-69,94-138`: collect rewrite and path-name shadowing; other pattern variables reference outer bindings.
- `community/cypher/front-end/ast/src/main/scala/org/neo4j/cypher/internal/ast/semantics/SemanticExpressionCheck.scala:612-622`: the projection is checked in the simple (non-aggregate) expression context and its type is wrapped in a list.
- `community/cypher/interpreted-runtime/src/main/scala/org/neo4j/cypher/internal/runtime/interpreted/pipes/ExpandAllPipe.scala:49-71` and `community/cypher/runtime-spec-suite/src/test/scala/org/neo4j/cypher/internal/runtime/spec/tests/ExpandAllTestBase.scala:566-580`: NULL start yields zero inner matches.
- `community/cypher/interpreted-runtime/src/main/scala/org/neo4j/cypher/internal/runtime/interpreted/pipes/RollUpApplyPipe.scala:33-42` and `community/cypher/runtime-spec-suite/src/test/scala/org/neo4j/cypher/internal/runtime/spec/tests/RollUpApplyTestBase.scala:59-78,102-123`: collection is empty for zero matches and preserves projected NULLs. Together with the rewrite, these establish the empty-list result for NULL outer entities.

## Reduce and list predicates (#1736, GQ-C2)

`GqlReduceExpression` carries accumulator, seed, iterator, source and body. The sealed
`GqlListPredicateExpression` carries GqlListPredicateKind (All/Any/None/Single), iterator,
source and required predicate. Both retain source spans, render through path and source-free
projection renderers, and participate in structural expression comparison. The profile
advertises REDUCE and LIST PREDICATE capabilities. Reduce is a lexical function spelling;
list predicate words remain keywords. They never enter the developer function catalog.
The parser distinguishes `all(...)` from projection/aggregate ALL, including inside scalar
and developer calls. At a set-quantifier position, a top-level WHERE distinguishes the list
predicate; existing `RETURN ALL (1+2)` and `count(ALL (x IN [1]))` remain grouped arguments.
Without WHERE, an ALL-prefixed group in those positions retains its prior set-quantifier meaning. Malformed implemented forms use CG00002 rather than CK00001. UNWIND
preserves the first expression diagnostic rather than adding a second missing-name error.

The [dialect truth table](DIALECT.md#reduce-and-list-predicates) defines NULL, empty and
short-circuit results. Reduce evaluates the source before the seed; NULL skips seed/body,
empty returns the seed, and each ordered body result becomes the next accumulator without
coercion to the seed type. Both declarations shadow outer names in the body only. Distinct
accumulator and iterator names are required; quoted/function-spelled names follow ordinary
identifier rules. Source/seed expressions may contain aggregates in aggregate-enabled
positions. Bodies reject native/developer aggregates before execution (CG05025), including
nested sources inside a body. Missing/duplicate declarations use CG01018. Invalid source
kinds use CG02015, statically when known. Predicate Boolean/NULL checks reuse CG02013;
unknown kinds defer to visited-element evaluation. All scopes and parameter references
validate even for NULL/empty sources and EXPLAIN.

Graph reuses GQ-C1 iterator frames, lexical reference walks, grouping rules and the statement
budget. Accumulator shape closure includes the seed and every possible step, across container
depths up to the existing shape horizon. Possible entity/composite/group shapes grow to a
fixed point; scalar/entity-kind proofs only weaken. Beyond that horizon possible shapes are
unknown and positive proofs are withheld. This permits changing accumulator types without
letting empty inputs or later wrapping/unwrapping evade entity wire admission. A same-kind
entity accumulator retains its proof through WITH/NEXT; scalar group properties cross Execute.
ORDER BY source/seed shapes resolve projected aliases against the original incoming scope;
lexical declarations still shadow those aliases in bodies, including nested forms. The shared
comprehension validator uses the same source-shape seam so a surrounding comprehension cannot
lose an entity alias's element kind before a nested C2 form binds it.
No public interface, wire capability, protocol version or disk format is added.

Reference evidence, re-derived in the local Neo4j clone (paths relative to its root; no code copied):

- `community/cypher/front-end/parser/v25/parser/src/main/antlr4/org/neo4j/cypher/internal/parser/v25/Cypher25Parser.g4:727-729,745-752`: reduce and list-predicate binding syntax. Cohesion requires WHERE, as Neo4j's semantic predicate-defined check does (`SemanticExpressionCheck.scala:1115-1119`).
- `community/cypher/interpreted-runtime/src/main/scala/org/neo4j/cypher/internal/runtime/interpreted/commands/InList.scala:46-57,69-83,93-106,117-132,143-163`: NULL sources, empty identities, decisive results and SINGLE's NULL/second-TRUE rule.
- `community/cypher/interpreted-runtime/src/main/scala/org/neo4j/cypher/internal/runtime/interpreted/commands/expressions/ReduceFunction.scala:35-46`: source-first NULL handling, one seed evaluation, ordered accumulator updates and empty seed identity.
- `community/cypher/front-end/ast/src/main/scala/org/neo4j/cypher/internal/ast/semantics/SemanticExpressionCheck.scala:696-720`: seed/source outer scope, scoped declarations and result type union. Cohesion deliberately permits arbitrary supported accumulator type changes rather than Neo4j's seed/body coercion restriction, as required by this run.

GqlReduceParserTests, the profile execution corpus, GraphReduceTests, GraphReduceWireTests
and the Graph NativeAOT sample pin grammar, source/seed order, lexical restoration, NULL truth
tables, nonexecuting EXPLAIN, type changes, static failures, rollback and both transports.

## Core scalar functions and CAST (#1148)

`GqlFunctionCallExpression(Function, Arguments)` resolves scalar names to `GqlScalarFunction`;
`GqlCastExpression(Operand, Target)` resolves explicit conversions to `GqlCastType`. Both copy
or retain immutable expression children. CURRENT_DATE and CURRENT_TIMESTAMP use function nodes
whose `IsNiladic` flag is true; their spelling has no parentheses. All niladic temporal
spellings (also CURRENT_TIME, LOCAL_TIME and LOCAL_TIMESTAMP) are ISO reserved words:
unquoted binding sites fail with CG00002; quoted names bind and are referenced quoted.
CAST is contextual: only CAST followed by `(` starts a conversion; `MATCH (cast:Movie)
RETURN cast.title` binds normally. Other registered function words remain legal variables,
path names, labels, keys and aliases. Scalar dispatch is separate from aggregate dispatch; adding a scalar
cannot silently create COLLECT_LIST. Names are case-insensitive, bindings remain ordinal.

The static language signatures check parsed arity with **CG00002**, exactly where #1147 checks
aggregate arity. Hand-built calls are checked again during planning with CG05001. Unknown
calls use **CG00010** naming the function. Recognized unsupported calls keep CK00001 naming
FUNCTION and the spelling; these table entries apply only before `(` in value positions, so
`date` and `duration` remain legal variables. `id(n)` specifically directs callers to element_id.
CURRENT_TIME, LOCAL_TIME and LOCAL_TIMESTAMP are recognized unsupported niladic expressions.
CARDINALITY, LTRIM/RTRIM/BTRIM, LEFT/RIGHT, LOG10 and Cypher toStringList/toIntegerList/
toFloatList/toBooleanList are recognized unsupported calls. Procedures/CALL, temporal
constructors, ELEMENTS and head/last/tail/reverse/range remain outside this subset.

The table distinguishes ISO clause 20 forms, direct Cypher aliases, and Cohesion extensions.
**Every scalar is strict:** a NULL argument returns NULL before kind or domain checks; every
argument expression still evaluates. Niladic datetime readers are the only exception. All
referenced bindings and signatures validate even in skipped branches or empty matches.
The main expression walk checks each reference once and carries its enclosing function for
diagnostics; nested calls do not rewalk descendants. KnownFunctionKind distinguishes integral
from non-integral numbers for toBoolean, rejects Boolean numeric CAST and temporal MATCH/INSERT
property values with CG03003 before writes.

| ISO canonical or extension | Accepted alias / status | Arguments | Non-NULL result |
| --- | --- | --- | --- |
| `element_id` | ISO 20.10; `elementId` is a 1:1 Cypher alias | node or relationship | database-local stable string identity; entity expressions also accepted as a Cohesion extension to ISO's variable operand |
| `labels` | Cypher spelling, Cohesion extension | node | ordinal label list |
| `type` | Cypher spelling, Cohesion extension | relationship | type string |
| `properties` | Cypher spelling, Cohesion extension | node, relationship or map | read-only property map copy |
| `keys` | Cypher spelling, Cohesion extension | node, relationship or map | ordinal key list |
| `startNode`, `endNode` | Cypher spellings, Cohesion extensions | relationship | stored source/target node from the same statement snapshot |
| `path_length` | ISO 20.22; `length` is a 1:1 Cypher alias | path | Int64 relationship count |
| `nodes` | Cypher spelling, Cohesion extension | path | non-null nodes in traversal order; aliases retain the element proof |
| `relationships` | Cypher spelling, Cohesion extension; `edges` is a Cohesion alias | path | non-null relationships in traversal order, retaining stored direction; aliases retain the proof |
| `lower`, `upper` | ISO 20.25; `toLower`, `toUpper` are 1:1 Cypher aliases | string | invariant-case string |
| `trim` | ISO 20.25 subset | string | string with leading/trailing U+0020 spaces removed |
| `substring` | Cypher spelling, Cohesion extension | string, numeric start [, length] | string sliced by Unicode code points, **zero-based**; past-end gives empty string |
| `replace` | Cypher spelling, Cohesion extension | string, search string, replacement string | ordinal replacement; empty search inserts replacement at every boundary, including both ends |
| `char_length`, `character_length` | ISO 20.22 synonyms | string | Int64 Unicode code-point count |
| `size` | ISO 20.22 for lists; Cypher string overload is a Cohesion extension | list or string | Int64 item/code-point count |
| `abs`, `ceil`, `ceiling`, `floor` | ISO 20.22; CEIL/CEILING are synonyms | number | input numeric type; unsigned inputs become Decimal |
| `round` | Cypher spelling, Cohesion extension | number [, nonnegative whole precision] | input numeric type; unsigned inputs become Decimal; half **away from zero** |
| `sqrt` | ISO 20.22 | nonnegative number | finite Double; negative input is CG02013 |
| `ln`, `log` | ISO 20.22 LN(value), LOG(base,value); Cypher log(value) is a Cohesion overload | positive number(s); log base positive and unequal to 1 | finite Double; invalid domain is CG02013 |
| `mod` | ISO 20.22 canonical function for Cypher `%` | two numbers | Double if floating, Decimal if Decimal/UInt64, otherwise checked Int64 |
| `CAST(value AS type)` | ISO 20.8, shared SQL conversion rules | scalar or NULL | requested scalar target; invalid value is CG02013 |
| `toString` | Cypher spelling, Cohesion extension | number, Boolean, string or temporal | invariant text; Boolean is lowercase |
| `toInteger` | Cypher spelling, Cohesion extension | number, Boolean or string | Int64, truncating fractions toward zero; unparsable text gives NULL |
| `toFloat` | Cypher spelling, Cohesion extension | number or string | finite Double; unparsable text gives NULL |
| `toBoolean` | Cypher spelling, Cohesion extension | Boolean, integer or string | Boolean; zero is false; unparsable text gives NULL |
| `CURRENT_DATE` | ISO 20.27, no parentheses | none | UTC DateOnly at transaction start |
| `CURRENT_TIMESTAMP` | ISO 20.27, no parentheses | none | UTC DateTimeOffset at transaction start |

ISO status was checked against the [published clause-20 contents preview](https://cdn.standards.iteh.ai/sist-preview/76120/a5695073ca0a485c9929f50970f4c862/ISO-IEC-39075-2024.pdf)
and [openGQL's clause-20 grammar](https://github.com/opengql/grammar/blob/main/GQL.g4#L2256-L2645),
not the paywalled normative text. Labels/type/keys/properties/startNode/endNode, substring,
replace and the four toX conversions have no corresponding function production there and are
explicitly Cohesion extensions. ISO supplies LEFT/RIGHT rather than SUBSTRING; the SQL
`SUBSTRING(s FROM i FOR n)` spelling is not accepted here. Cypher substring always starts at 0. Like Neo4j, floating coordinates truncate toward zero before checking the resulting nonnegative Int32 range; negative resulting coordinates or out-of-range indexes/lengths are CG02013.

CAST targets are INT/INTEGER (Int32), INT64/BIGINT (Int64), REAL (Single), FLOAT/FLOAT64/
DOUBLE [PRECISION] (Double), DECIMAL/NUMERIC, STRING/CHAR/VARCHAR (unbounded string), BOOL/
BOOLEAN, DATE, ZONED DATETIME and TIMESTAMP WITH TIME ZONE (DateTimeOffset). Type modifiers,
LIST, DURATION, BYTES, ZONED TIME, INT32, SMALLINT, FLOAT32, TIMESTAMP WITHOUT TIME ZONE
and LOCAL DATETIME are outside this subset; recognized unsupported target
families use CK00001, unknown target spellings use CG00002. Numeric-to-integer CAST rejects
fractions, including fractional text; it never truncates or rounds. It follows SQL #1119's
shared storage types and value rules. Numeric text is invariant without grouping, and exact
numeric text cannot silently round into a whole integer. DOUBLE/REAL reject nonzero underflow
to zero from text or numeric input with CG02013. DECIMAL-to-REAL parses invariant decimal
text directly to avoid double rounding, matching SQL SqlStandardCasts.cs:161,206-249. Temporal text follows SQL's invariant
ISO date/time grammar, absent offset means UTC, and dates convert to midnight UTC. Boolean
CAST accepts Boolean or TRUE/FALSE text; CAST to string renders Boolean uppercase. Timestamp
text uses SQL resources/Database/Assimalign.Cohesion.Database.Sql/src/Internal/Types/SqlCastText.cs:27's FFFFFFF fraction, omitting the fraction at whole seconds. Cypher
conversions deliberately differ from CAST: toInteger truncates, accepts Boolean as 1/0, and
toInteger/toFloat/toBoolean return NULL for unparsable text. String/Decimal range overflow and non-finite
results still fail with CG02013. Like Neo4j, toInteger saturates finite floating values outside Int64 range; CAST rejects them. toString retains a fractional digit for floating integers and uses Cypher scientific-notation thresholds. This implements the main-session ruling over the older
issue-body default that requested CAST semantics for toInteger/toFloat.

Math retains exact integer identity above 2^53. abs/ceil/floor/round retain the input CLR
numeric type (all unsigned inputs become Decimal). Neo4j instead returns Float for ceil,
floor and round; this is a documented Cohesion divergence. Round uses half away from zero,
including negative ties, rather than Neo4j's unary ties toward positive infinity. Approximate round
uses the value's round-trip decimal form with bounded digit work, including precisions above 15.
SQRT/LOG/LN domain errors, zero divisors, overflow, invalid dynamic kinds, and excessive string
results use CG02013; they never return NaN or infinity. REPLACE bounds expanded UTF-8 bytes
before allocating, including empty-search insertion (replace('abc','','X') is 'XaXbXcX';
Neo4j community/values/src/main/java/org/neo4j/values/storable/StringValue.java:197).
toBoolean's integral-only numeric signature follows CypherFunctions.java:2775. Existing composite limits apply to every function's output.

Planning rejects provably incompatible entity/path/group, literal, parameter and carried value
kinds with CG03003 naming the function. Unbound arguments use CG05001 naming the function;
dynamic property kinds are checked at execution with CG02013. EXPLAIN runs the same signature
and scope checks without evaluation. Properties(map) preserves nested composite/entity/group shapes while stored entity properties remain scalar; wrappers cannot bypass wire admission. Function identity and CAST target participate in expression
hashing/equality, so equivalent function GROUP BY keys match RETURN items. Child, group,
composite, entity, scalar, rendering and validation walks all recognize both new nodes.

GraphDatabaseSession captures one UTC instant from `EngineOptions.TimeProvider ?? TimeProvider.System`
**before Coordinator.BeginAsync**, once per explicit transaction. Autocommit captures at the same
point in GraphDatabase.BeginOperationAsync. GraphDatabaseTransaction and GraphOperation carry the
instant; GraphEvaluationContext passes it explicitly to every evaluator reader, including MATCH
maps, grouping and sorting. It never lives on TransactionContext: ReadCommitted replaces that
context per statement. Every row, stage and statement of one transaction sees the same instant;
each autocommit statement captures its own. This mirrors SQL #1120's capture/carrier/reader seam.
The existing clock remains internal; no public option or visibility grant is added. Graph wire
accepts START TRANSACTION / COMMIT / ROLLBACK over Execute; one explicit wire transaction shares that instant.

Temporal values compare and sort by DateOnly date or DateTimeOffset instant, including nested
composite sort keys. Total ordering puts zoned timestamps then dates between paths and strings;
DATE and timestamp are incomparable in predicates. Temporal results cannot become stored graph
properties: planning rejects proven temporal property kinds with CG03003; dynamic temporal values
raise CG02013 before the storage encoder, and the statement rolls back rather than leaking ArgumentException. No stored type, catalog version or wire encoding changes.

The wire Execute gate rejects entity-valued nodes/edges/relationships/startNode/endNode results
before writer locking or mutation, even on empty input. Scalar access through those results is
legal, and LET s=startNode(r) followed by RETURN s can use ExecutePaths when the binding is
mandatory. List/map functions labels/keys/properties use #1146's existing composite capability;
unnegotiated possible composites are CG07016. Scalar, CAST and temporal results use existing
components. Runtime errors materialize before ResultHeader and preserve the authenticated session.

Conformance lives in GqlFunctionParserTests, GqlKeywordDispositionTests, GraphFunctionTests,
GraphFunctionWireTests and GqlProfileExecutionTests' bidirectional Profile.Functions guard.
Local behavior references (consulted, no implementation code copied), relative to
C:/Source/repos/neo4j/neo4j: community/cypher/runtime-util/src/main/java/org/neo4j/cypher/operations/CypherFunctions.java:328-346,385-456
(numeric return types and rounding), :1748-1784 (substring), :1920-1935 (labels), :2486-2524
(path lists), :2675-2692 (size), :2730-2743 (length), :2764-2785,2814-2822,2881-2905 (conversions), :3485-3553 (numeric substring coordinates);
community/values/src/main/java/org/neo4j/values/storable/StringWrappingStringValue.java:99-116
(code-point slicing). Cohesion's CAST uses the local SQL precedent in
Sql/src/Internal/Types/SqlStandardCasts.cs:175-194,224-229 and Sql.Language/src/SqlLiteralText.cs:14-85;
the clock shape is recorded in Sql/docs/DESIGN.md, Planning, execution and bounds.

## Aggregates and grouping (#1147)

`GqlAggregateExpression(Function, Operand, Distinct)` represents ISO aggregate functions
separately from scalar calls. A null operand represents COUNT(*); only COUNT permits star,
and COUNT(DISTINCT *) or COUNT(ALL *) is CG00002. ALL is the default set quantifier; DISTINCT applies to
the non-null operand values. Function names remain valid node, edge and path variables,
property owners and aliases despite lexing as Function tokens. `collect` maps directly to
`GqlAggregateFunction.CollectList`, the ISO COLLECT_LIST operation.

Every RETURN item without an aggregate is an implicit grouping key. An explicit ISO
`GROUP BY` lists exactly those items' names, in any order; equivalent input expressions
are also accepted (structurally duplicate RETURN keys count once). A plain variable resolves
first to a RETURN item's name; an aggregate item's name is CG05001. Other expressions
retain input scope and cannot contain aggregates. `GROUP BY ()` is the empty grouping set,
valid only when there are no non-aggregate keys; otherwise it raises the same mismatch error.
A keyed empty input returns no rows; with no keys an aggregate always returns one row. GROUP BY without aggregates
eliminates duplicate keys. This implements the owner's grouping default. Mixed aggregate
expressions may use a key as a subtree or a grouped entity's properties; they cannot
introduce an ungrouped variable. RETURN aliases are available in GROUP BY as plain grouping
names and in ORDER BY, where they shadow input variables. RETURN itself retains input scope.
Computed-expression reuse in ORDER BY excludes expressions referencing redefined variables;
aggregate operands retain input scope. Function-named comprehension iterators parse normally.
Recursive aggregate parsing checks the thread stack.

All functions skip NULL values; COUNT(*) counts input rows, including OPTIONAL MATCH's
null-extended rows. DISTINCT and grouping reuse RETURN's exact numeric, composite and
entity equality; NULL keys group together. MIN/MAX use ORDER BY's total order, preserve
the selected value's runtime type and skip top-level NULLs. COLLECT_LIST preserves retained
input order and duplicates unless DISTINCT is specified; its result is an object array.

| Function | Result type | Empty or all-NULL input |
| --- | --- | --- |
| COUNT(*), COUNT(expr), COUNT(DISTINCT expr) | Int64, including wire column metadata | 0 |
| SUM | Int64 for integral inputs; Double when any retained input is floating; Decimal when any is Decimal or UInt64 (takes precedence over floating) | Int64 0 |
| AVG | Double for integral/floating inputs, including UInt64; Decimal if any retained input is Decimal | NULL |
| MIN, MAX | Selected input runtime type | NULL |
| COLLECT_LIST, collect | List, requiring graph.composite-values on the wire | Empty list |

These numeric types implement the owner's default. Decimal arithmetic converts any mixed
floating values to Decimal; an unrepresentable conversion fails. Integral/Decimal totals
are retained exactly until final promotion. Mixed integer/floating SUM converts the exact
integer quotient to Double once before adding floating values. Exact Decimal SUM retains the
largest input scale (integral inputs have scale zero); only Decimal averages round once, ties to even,
to the available Decimal precision. Double averages use a bounded running mean. Invalid
SUM/AVG operands, non-finite results and numeric overflow are CG02013, #1140's value
error code; a non-numeric operand message names SUM or AVG. Floating inputs outside Decimal's
range are detected before conversion, avoiding per-row overflow exceptions. Negotiated Graph
value encoding preserves Decimal scale through parameters and results. Nested aggregates,
aggregates in WHERE, unequal GROUP BY lists, and ungrouped
ORDER BY references are CG05001 before reading data, including under EXPLAIN. Parameters
inside aggregate operands are validated even on empty input. ExecutePaths rejects a final
aggregate projection with its existing CG05001 path-request message; an intermediate
aggregate such as WITH collect(n) AS ns UNWIND ns AS n feeds later stages (#1145). HAVING, STDDEV_SAMP, STDDEV_POP,
PERCENTILE_CONT and PERCENTILE_DISC remain precise CK00001 capability diagnostics.
WITH aggregates execute through the same per-stage grouping plan (#1145).

Neo4j behavior consulted, no code copied, relative to `C:/Source/repos/neo4j/neo4j`, under
`community/cypher/interpreted-runtime/src/main/scala/org/neo4j/cypher/internal/runtime/interpreted/pipes/aggregation/`:
`CountFunction.scala:31-40` skips NULL and returns Long; `SumFunction.scala:36-45` returns
zero for empty input; `AvgFunction.scala:53-57,69-70` returns NULL or Double and updates
a running mean; `MinMaxFunction.scala:35-49` retains NULL until a non-null input;
`CollectFunction.scala:32-43` builds a list and skips NULL. Decimal promotion is Cohesion's
extension to those numeric types. GraphAggregateTests, GraphAggregateWireTests, the keyword
corpus and the single function-coverage guard pin the executable surface.

The star production has no set quantifier, as in Neo4j
`community/cypher/front-end/parser/v25/parser/src/main/antlr4/org/neo4j/cypher/internal/parser/v25/Cypher25Parser.g4:812-813`.

Additional local references consulted, no code copied: Neo4j
`community/values/src/main/java/org/neo4j/values/utils/ValueMath.java:60-65` adds mixed numeric
values directly; `community/cypher/front-end/rewriting/src/main/scala/org/neo4j/cypher/internal/rewriting/rewriters/preparatoryRewriters/NormalizeWithAndReturnClauses.scala:315-342`
guards reuse against redefined variables; `community/cypher/front-end/frontend/src/main/scala/org/neo4j/cypher/internal/frontend/phases/parserTransformers/scoping/AggregationChecker.scala:117-144`
distinguishes grouping and aggregate aliases. PostgreSQL, under `C:/Source/repos/postgres/postgres`,
`src/backend/utils/adt/numeric.c:8153` retains the larger numeric input scale.

## Ordering and paging (#1142)

RETURN accepts `return-tail` in both row and path requests. `GqlQueryExpression.OrderBy`
contains `GqlSortItem(Expression, Direction, NullOrdering)`; Offset and Limit contain literal
or parameter expressions. ASC and ALL are the defaults. NULLS, FIRST and LAST are contextual
words, absent from Profile.Keywords, so `first` and `last` remain valid variable names.
OFFSET and Cypher SKIP share one slot. OFFSET/SKIP must precede LIMIT, as in ISO and Cypher; LIMIT followed by OFFSET/SKIP, duplicate clauses
and ORDER BY following paging are CG00002 at the offending token. A negative, floating or
string literal is CG00002 expecting an unsigned integer or parameter; overflow is CG00004.
Dynamic values must have an integral CLR type in [0, Int64.MaxValue]; NULL, negatives,
floating values and other graph values fail with CG02013 before reads or writes. Non-finite and unsupported CLR values fail with CG03003 during binding. Missing parameters
remain CG05001. Both `$name` and `@name` follow the shared named-binding contract.

The owner default follows Cypher: NULL last ascending, first descending. NULLS FIRST/LAST
overrides that direction-dependent placement. This diverges from Cohesion SQL's NULL-first ASC
default. Strings compare ordinally; numbers use the owner's exact comparison everywhere,
including 0.1m versus 0.1d and adjacent integers above 2^53. The complete ascending family
order is map, node, relationship, list, path, zoned timestamp, date, string, Boolean, number, NULL. Lists compare
lexicographically; maps compare count, sorted ordinal keys, then corresponding values;
nested values use the same total order with NULL last. Entities compare IDs; paths compare
their starting node, relationship sequence and length. Equal sort keys retain input order.

ORDER BY sees RETURN aliases and projected expressions. Without DISTINCT it may also use
unprojected bindings. With DISTINCT, every referenced binding must come from a projected
value, including a projected entity under any alias or a structurally matching computed expression (literal type and value both match); a hidden
binding is CG05001 at planning even on an empty graph. Aliases remain unavailable to other
RETURN items. CASE, COALESCE and composite expressions are ordinary sort keys. A label test on an alias that may produce an entity is validated at runtime; a non-entity value raises CG03003.

Mutations run for every matched binding before projection. DISTINCT precedes sorting, then
OFFSET precedes LIMIT. LIMIT 0 returns no rows; large offsets return no rows without overflow.
Read bindings stream. An unordered LIMIT 1 can complete before expanding all matches; ORDER BY
consumes its required input and a finite page retains a stable top set of OFFSET + LIMIT rows.
Final results remain materialized under the statement memory budget (CG05020); selected-candidate
work and the 64-hop limit retain CG05004. Sorting does not change wire or catalog formats. Standalone ORDER BY/page statements
before RETURN remain CK00001 naming STANDALONE ORDER BY AND PAGE STATEMENT (T2).
WITH ordering/paging executes through the same stage, and aggregate aliases follow its grouping stage.

Behavior consulted, no code copied, relative to `C:/Source/repos/neo4j/neo4j`:
`community/values/src/main/java/org/neo4j/values/AnyValueComparator.java:44-85`,
`virtual/VirtualValueGroup.java:26-32`, `virtual/MapValue.java:538-564`, and
`virtual/PathValue.java:171-189` under the same values tree define the total family/container
order. Neo4j's [ORDER BY documentation](https://neo4j.com/docs/cypher-manual/current/clauses/order-by/)
records NULL placement and RETURN alias/hidden-variable visibility, including DISTINCT.
`community/cypher/front-end/ast/src/main/scala/org/neo4j/cypher/internal/ast/Clause.scala:1965-2025`
checks ORDER BY in the projected scope, retaining the incoming scope only without DISTINCT or aggregation.
`Cypher25Parser.g4:138-164` under `community/cypher/front-end/parser/v25/parser/src/main/antlr4/org/neo4j/cypher/internal/parser/v25/`
fixes paging order and accepts both direction spellings.
`NormalizeWithAndReturnClauses.scala:253-347` under
`community/cypher/front-end/rewriting/src/main/scala/org/neo4j/cypher/internal/rewriting/rewriters/preparatoryRewriters/`
uses typed AST hash keys and rewrites projected entity references.
`SemanticExpressionCheck.scala:552-564` under
`community/cypher/front-end/ast/src/main/scala/org/neo4j/cypher/internal/ast/semantics/`
permits label operands that may be entities. Behavior consulted, no code copied.
GqlOrderPagingParserTests, GqlKeywordDispositionTests, GqlProfileExecutionTests,
GqlOrderPagingTests and GraphOrderPagingWireTests pin the executable surface.

## Diagnostics and conformance (#194, #195)

| Code | Meaning |
| --- | --- |
| `CK00001` | Recognized construct outside the executable profile, reported once at its own span: OPTIONAL block operands, MANDATORY MATCH, procedures, excluded functions and datetime forms, unsupported CAST targets, HAVING and statistical aggregates (STDDEV_SAMP/POP, PERCENTILE_CONT/DISC), substituted/whole-map parameters, composite properties, map projections, questioned paths, subpath variables, nested quantifiers, standalone/global composite ORDER BY/page statements, FOR ordinality/offset suffixes, NEXT YIELD, server/session scope, undirected (tilde) edges, pattern-comprehension path prefixes, nested path modes/selectors, unselected unbounded WALK, counted ANY and `NODETACH DELETE` |
| `CG00001` | Empty statement |
| `CG00002` | Malformed supported syntax, invalid statement composition, leftover tokens, or a character GQL does not use (`?`, `#`, `^`, ```, ...) |
| `CG00003` | Unterminated string, quoted name, or block comment |
| `CG00004` | Invalid or out-of-range numeric literal |
| `CG00005` | Repetition-bound sum exceeded: a path contributes at most 64 relationships through fixed edges and quantified bounds. Label expressions and predicates have no length or nesting limit (#1139 follow-up) |
| `CG00006` | Duplicate literal property or map-literal key |
| `CG00007` | `Graph catalog introspection is read-only.`: mutation composed with `SHOW` |
| `CG00008` | A `--` comment begins exactly where a node/subpath pattern's `)`, an edge pattern's `]`, or a postfix quantifier's `}`, `+` or `*` ends, in `MATCH`, `INSERT` or `CREATE`: a Cypher arrow (`-->`, `--`) that would hide the rest of the line. Reported at the `--`; the message names `->`, `<-` and `-` |
| `CG00009` | The statement nests groups, unary expressions, CASE, subscripts or collection constructors deeper than the parsing thread's stack can follow. Reported at the group, unary, constructor or subscript token that could not be entered; the parse stops there and reports nothing else. The same text parses on a thread with more stack, so the engine's `GraphQueryRequest.FromGql` reports it as `CG05008`, statement too complex, not as a parse error |
| `CG00010` | Unknown function call; the message names the unregistered spelling |
| `CG00011` | A counted SHORTEST selector omits a positive Int64 literal count or supplies zero, a sign, a non-integer, a parameter or overflow |
| `CG00012` | A list slice omits both start and end bounds; rejected during planning, including EXPLAIN |
| `CG00013` | Language/syntax — EXISTS omits its nonempty braced pattern |
| `CG00014` | Pattern comprehension requires a nonempty path and projection |

Locations use zero-based absolute UTF-16 offsets with exclusive ends and one-based line numbers.
A line breaks at LF, CR, NEL (U+0085), LS (U+2028) or PS (U+2029), and CR LF is one break
(`TokenLexer.CountLineBreaks`), so lines break exactly where a `--` comment ends.
Capability checks run before syntax parsing, so a recognized unsupported clause does not degrade
into a generic error caused by its downstream syntax. Quoted text, comments, label names, and
property keys do not trigger keyword-based capability checks. Binding and catalog diagnostics belong
to the Graph planner, which knows the database schema.

### Keyword disposition (#1101)

The capability scan reads one static recognized-unsupported table, `GqlUnsupportedVocabulary`.
Each entry has a spelling, the construct its `CK00001` names, and a position. Each construct is
reported once, at its own span. The pattern parser separately owns nested-mode and unbounded
WALK capability checks; executable mode words no longer occur in the unsupported table (#1733):

| Written | Construct named | Span | Who lifts it |
| --- | --- | --- | --- |
| `(a)~[r]~(b)`, `<~[r]~`, `~[r]~>`, `(a)~(b)`, `<~`, `~>` | `UNDIRECTED EDGE` | the whole edge, from `~` or `<~` through its closing `~` or `~>` | pin until the engine stores undirected edges |
| `MATCH (TRAIL (a)-[]->(b))`, nested WALK/SIMPLE/ACYCLIC | `<MODE>` | the inner mode word | CK00001; outer modes execute (#1733) |
| `MATCH ALL SHORTEST (...)`, `MATCH ANY SHORTEST (...)` | selector AST | complete prefix span | executes per endpoint pair under statement budgets |
| `MATCH SHORTEST 2 (...)`, `SHORTEST 2 GROUPS` | counted selector AST | complete prefix span | executes per endpoint pair under statement budgets |
| `MATCH ALL (...)`, `MATCH ALL TRAIL (...)`, `MATCH ANY (...)` | selector AST | complete prefix span | executes per endpoint pair under statement budgets |
| `MATCH ANY 2 (...)` | `ANY PATH SEARCH` | complete prefix span | outside #1734 |
| `MATCH (n) NODETACH DELETE n` | `NODETACH DELETE` | both words | pin |
| `MERGE (n)` | MERGE stage AST | complete clause | executes whole-pattern match/create (#1735) |

#1101 pinned the label-expression operators (`LABEL DISJUNCTION`, `LABEL CONJUNCTION`,
`LABEL NEGATION`, `WILDCARD LABEL`) and `(n IS A)` (`IS LABEL EXPRESSION`) here; #1139 made them
executable, removed them from the table and flipped their corpus cases to supported parses. A
`[:A|:B]` relationship alternative, which the old pin also covered, is `CG00002` now: ISO writes
`[:A|B]`. A path mode word
was read as a path variable, so `MATCH TRAIL (...)` failed with "Expected '='". `ALL SHORTEST`
reported two diagnostics, `ALL` and `SHORTEST PATH`, and a path mode after a path search prefix
(`ALL TRAIL`, `ANY SHORTEST TRAIL`) was a second construct; ISO/IEC 39075 makes the whole
`<path search prefix>` one. `NODETACH` failed with
"MATCH requires RETURN, INSERT, CREATE, or DELETE.". `STARTS WITH` and `ENDS WITH` are one
construct each, and a procedure's `YIELD` belongs to its `CALL`. A statement whose first word is
unsupported, such as `SESSION SET GRAPH g` or `DROP GRAPH g`, is one construct: the scan
reports that word and nothing after it.

Path mode words and `NODETACH` are positional, not lexer keywords. Before a path pattern, modes
select identity rules; before `DELETE`, NODETACH names its refused construct. Elsewhere they are names, so `MATCH trail = (a)-[]->(b)`
and `MATCH (nodetach) DELETE nodetach` still parse.
`GqlTransactionVocabulary` lists the contextual words of standalone controls, including the
optional TRANSACTION after BEGIN; these do not become lexer keywords. Their standalone parser
branch preserves the existing coded refusals of transaction options and composition.
`GqlKeywordDispositionTests` enumerates the profile's keywords, both word tables, the path-mode enum, and the executable
label-expression spellings `GqlLabelVocabulary` lists (`|`, `&`, `!`, `%` and the positional
`LABELED`). Every word needs a supported parse case or a case with exactly one `CK00001` naming its
construct, and for a table entry that diagnostic must also name the construct the table records. A
word added without a case fails. `~` is a table entry (`UNDIRECTED EDGE`); `--` after a pattern
element is `CG00008`, which "Label expressions and edge directions" below describes.

A character GQL does not use lexes as `TokenType.Unrecognized`. It reports
`Unexpected character '<c>'; it is not part of GQL.` (`CG00002`) at its span during
tokenization, and the statement is not parsed, so nothing is bound. `?` defers rejection to
syntax parsing: an operand gets the shared supported-form message, while a trailing stray
`?` keeps the generic character message. Keyword-shaped host parameters such as `:union`
receive one CG00002 at the whole reference, without a capability diagnostic for the name.
Every statement form
(`MATCH ... RETURN`, `INSERT`, `CREATE`, `DELETE`, `DETACH DELETE`, `SHOW`) rejects leftover
tokens with `CG00002`, with or without a separating `;`, and `GqlParseStrictnessTests` pins each.

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
comment swallowed it too and the statement failed with `CG00002`.) The `WHERE` now stays in effect
at every terminator. `GqlLineCommentTests` pins both delete forms, the clause after a spaced
comment (`MATCH (a) -- c<CR>RETURN a`, the ISO case #1139 keeps), and diagnostic lines at each
terminator.

The rule moves only where a comment ends. `CG00008` (#1139) keys on where a `--` comment starts,
so it holds whichever terminator ends the comment. Between #1150 and #1139, `(a)-->(b)` followed
by any line terminator read as `(a)` plus a comment, so
`MATCH (a:Person)-->(b:Person)<CR>DETACH DELETE a` ran as `MATCH (a:Person) DETACH DELETE a` and
deleted every Person. It is now `CG00008` at every terminator, and `GqlCypherArrowTests` and the
engine's `GqlLabelDirectionExecutionTests` pin it at each one.

ISO also spells a simple comment `//`. The shared lexer has no `//` comment, so
`MATCH (a) // note` reports `CG00002` at the first `/`. An item that adds the `//` form adds a
per-language lexer switch and ends the comment at the same terminators.

## Label expressions and edge directions (#1139)

The profile advertises `LABEL EXPRESSION`: ISO/IEC 39075 16.8 label expressions in node and edge
patterns, G074's wildcard `%`, and the `<labeled predicate>` in `WHERE`. The parser binds `!`
tighter than `&` and `&` tighter than `|`; a run of one operator is one n-ary node. The
engine evaluates a node's expression against its label set and a relationship's against its one
type, with a typed switch over the five records and no reflection or text matching:

| Expression | A node matches when | A relationship matches when |
| --- | --- | --- |
| `A` | it carries `A` | its type is `A` |
| `%` | it carries at least one label | always: every stored relationship has a type |
| `!e` | it does not match `e`, so `!%` selects unlabeled nodes | it does not match `e`, so `!%` selects none |
| `e & f` | it matches both | it matches both, so `T&U` selects none |
| `e \| f` | it matches either | it matches either |

Every name in a `MATCH` expression resolves against the catalog, including names under `!` and `|`,
because labels are persistent catalog definitions. A name the database does not have is a label no
node carries, or the type of no relationship: the expression evaluates with it (`:!Missing` matches
every node), and a read reports it as a `CG01010` or `CG01011` warning instead of failing, as
Neo4j does (#1228; Graph engine design, "Unknown labels and relationship types in reads"). An `INSERT`
node takes a pure conjunction (`:A`, `:A&B`, `:A:B`) and may introduce new labels; `|`, `!` and
`%` select nodes and cannot label a new one, so they are `CG05001`. An inserted relationship
needs one type. A labeled predicate (`n IS LABELED A|B`, `n IS NOT LABELED A`, `n:A|B`) resolves
its names against the catalog of the variable's kind, a path variable is `CG03003`, and an
unbound one `CG05001`; a variable null-extended by OPTIONAL MATCH yields
UNKNOWN. The #1140 conformance corpus adds the `NOT`/`OR` combinations
with labeled predicates. A `:` directly after an element variable in a predicate is the labeled
predicate; an operand that begins with `:` keeps shared-parameters' coded `:name` rejection, and
whichever of the two lands second adds the other's case.

**Index and label-scan anchors (#1245).** Pure pattern conjunctions and non-negated labeled
predicates in the clause's top-level AND chain supply required node labels. Thus `(n:A&B {k: 1})`
and `MATCH (n) WHERE n IS LABELED A&B AND n.k = 1` may seek an index of `A` or `B`.
Pattern labels precede WHERE labels in source order. Bound entities take precedence, then property
seeks, then the smallest retained-posting estimate across required labels and pure pattern
unions at any node position (#1534). `(a)->(b) WHERE b:A` may
therefore anchor at `b`, as `(a)->(b:A)` does. The full pattern and predicate remain residual
checks. EXPLAIN reports the selected label and node ordinal. Requirements are clause-local;
relationship labeled predicates supply no node labels. OR, XOR, NOT, truth tests, IS NOT LABELED
and label expressions using `|`, `!` or `%` supply no required labels. Pure pattern unions
such as `(n:A|B {k: 1})` merge MVCC label-posting ranges with duplicate suppression;
conjunctions scan the cheapest required label and retain the complete residual. EXPLAIN
reports union labels as `A|B` with `combine=union`. Negation and mixed expressions retain
a full scan unless another safe anchor narrows them. Counts are advisory retained-posting
estimates, not snapshot-specific result counts.
Behavior consulted, no code copied: Neo4j's
`community/cypher/cypher-planner/src/main/scala/org/neo4j/cypher/internal/compiler/planner/logical/steps/leafplanner/labelScanLeafPlanner.scala:45-46, :73-78`
reads HasLabels selections for any pattern node (relative to C:/Source/repos/neo4j/neo4j).

**Directions.** Storage holds only directed relationships, so the ISO forms map as follows:

| Written | `GqlPatternDirection` | Matches | Inserts |
| --- | --- | --- | --- |
| `-[]->`, `->` | `Outgoing` | the stored edge from the preceding node | yes, with one type |
| `<-[]-`, `<-` | `Incoming` | the stored edge into the preceding node | yes, with one type |
| `-[]-`, `-` | `Undirected` (any direction) | either orientation | `CG05001` |
| `<-[]->`, `<->` | `LeftOrRight` | either orientation, exactly as `Undirected` | `CG05001` |
| `~[]~`, `<~[]~`, `~[]~>`, `~`, `<~`, `~>` | none | `CK00001` `UNDIRECTED EDGE` | `CK00001` |

One stored `A`-to-`B` edge therefore yields the rows `(A,B)` and `(B,A)` for `-[r]-`, `-`, `<-[r]->`
and `<->`, and a self-loop yields one row. The tilde forms are rejected rather than matched: `~[]~`
and `~` would match nothing, and `<~[]~` and `~[]~>` would silently equal their directed forms. The
rejection applies only in pattern position, directly after a node's `)`; a `~` elsewhere is a
character GQL does not use (`CG00002`). Insertion rejects both either-direction forms, because
the executor would otherwise have to pick an orientation.

**Cypher arrows (D3).** ISO/IEC 39075 defines `--` as a `<simple comment>`, and the shared lexer
reads it that way in every language, so no lexer file changes and SQL and OQL comments cannot
regress. The parser records where each `--` comment starts and, where a node pattern consumes its
`)` or an edge pattern its `]`, reports `CG00008` at the `--` when a comment starts at exactly that
offset. That turns the silent truncation `MATCH (b:Person)-->(a)<LF>RETURN b` →
`MATCH (b:Person) RETURN b` into an error at every line terminator. Comments elsewhere keep their
ISO meaning: `(a) -- note`, a comment on its own line, and `WHERE (a.age > 1)-- note`, whose `)`
closes a predicate. Under D3 these Cypher spellings have no 1:1 ISO counterpart and are not
accepted:

| Cypher | Read by GQL as | Result | ISO spelling |
| --- | --- | --- | --- |
| `(a)-->(b)` | `(a)` then a comment | `CG00008` | `(a)->(b)` |
| `(a)--(b)` | `(a)` then a comment | `CG00008` | `(a)-(b)` |
| `(a)-[r]-->(b)`, `(a)-[r]--(b)` | `-[r]` then a comment | `CG00008` | `-[r]->`, `-[r]-` |
| `(a)<--(b)` | `<-` then `-` | `CG00002` | `(a)<-(b)` |
| `(a)<-->(b)` | `<-` then `->` | `CG00002` | `(a)<->(b)` or `(a)-(b)` |

The residual is recorded rather than guessed at: whitespace or a block comment between the element
and the dashes, as in `(a) -->(b)` or `(a)/* c */-->(b)`, leaves an ISO comment, so the rest of
that line is still comment text. `GqlCypherArrowTests` pins that residual so a change to it is
deliberate.

## Chain length and nesting (#1139 follow-up)

#1139 shipped binary `GqlLabelConjunction`/`GqlLabelDisjunction` records, and `WHERE` joined
predicates in a left-deep binary `GqlBinaryExpression` `AND` tree. Every walker recursed once per
operator, so the parser capped label trees, label parentheses, predicate parentheses and comparisons
at 128 each (`CG00005`) and the planner capped hand-built trees (`CG05001`). One chain therefore
held at most 128 labels or 128 comparisons, while the repeated-colon list had been unbounded before
#1139. The owner decision of 2026-10-02 was "do what Neo4j does". Neo4j has no such limit (paths are
in the Neo4j repository):

- Its label expression AST has n-ary `Conjunctions(children)` and `Disjunctions(children)` with
  `flat`, which unnests a same-operator child, beside the binary `ColonConjunction` and `Negation`
  (`community/cypher/front-end/expressions/src/main/scala/org/neo4j/cypher/internal/label_expressions/LabelExpression.scala:238-302,309-317,331`).
  `replaceColonSyntax` rewrites `:A:B` into `Conjunctions.flat` (`:202-208`).
- The Cypher 25 AST builder folds each run with `flat`: `A|B|C` in `exitLabelExpression4`, and
  `A&B&C` and `:A:B` in `exitLabelExpression3`
  (`community/cypher/front-end/parser/v25/ast-factory/src/main/scala/org/neo4j/cypher/internal/parser/v25/ast/factory/LabelExpressionBuilder.scala:144-189`).
  The grammar counts nothing (`community/cypher/front-end/parser/v25/parser/src/main/antlr4/org/neo4j/cypher/internal/parser/v25/Cypher25Parser.g4:515-532`).
- `WHERE` conjunctions become the n-ary `Ands` (`flattenBooleanOperators.scala:35-50` under
  `community/cypher/front-end/frontend/src/main/scala/org/neo4j/cypher/internal/frontend/phases/rewriting/cnf/`).
- Nesting is bounded only by the JVM stack. The parser has no depth check; a `StackOverflowError`
  reaches Bolt, which reports the non-fatal transient `Neo.TransientError.General.StackOverFlowError`
  (GQLSTATUS 51N37) and keeps the connection
  (`community/bolt/src/main/java/org/neo4j/bolt/protocol/common/message/Error.java:188-249`,
  `community/bolt/src/main/java/org/neo4j/bolt/fsm/StateMachineImpl.java:156-163`,
  `community/common/src/main/java/org/neo4j/kernel/api/exceptions/Status.java:667-674`).
- Storage does not cap labels per node: they sit in the node record while they fit 36 bits and spill
  to a chain of dynamic label records otherwise
  (`community/record-storage-engine/src/main/java/org/neo4j/kernel/impl/store/InlineNodeLabels.java:42,115-122`,
  `DynamicNodeLabels.java:105-229`).

Cohesion now does the same:

| | Before | Now |
| --- | --- | --- |
| `:A:B:...`, `A&B&...`, `A\|B\|...` | at most 128 names (`CG00005`) | one n-ary node of any length |
| `p AND q AND ...` in `WHERE` | at most 128 comparisons and labeled predicates (`CG00005`) | one `GqlLogicalExpression` of any length |
| Label parentheses and negations | at most 128 levels (`CG00005`) | as deep as the parsing thread's stack allows; deeper is `CG00009` |
| Predicate parentheses | at most 128 levels (`CG00005`) | as above |
| Hand-built tree depth | at most 128 label levels or 256 predicate levels (`CG05001`) | no limit; a walk out of stack is `CG05008` |

- **Flattening.** The parser builds each run of `|`, `&`, repeated `:` or `AND` in one list, so a chain
  costs time linear in its length. A parenthesized chain of the same operator that opens a run
  merges into it by handing over its list: `(A|B)|C` is the same node as `A|B|C`, as left
  associativity read it before. A group in a later position stays nested, `A|(B|C)`. Neo4j's `flat`
  also merges that one, but its builder re-flattens the growing vector at every operator, which is
  quadratic in a plain chain's length, and merging a later group would cost a copy of its operands
  per enclosing group; the meaning is identical because `&`, `|` and `AND` are associative. Flattening
  never crosses a precedence level: `A|B&C` is `Or(A, And(B, C))` and `!A&B` is `And(Not(A), B)`.
- **Nesting.** The label grammar recurses through parentheses; scalar expressions also recurse through NOT, signs and IN lists. Before each such descent the parser calls
  `RuntimeHelpers.TryEnsureSufficientExecutionStack`; on a thread out of stack it reports `CG00009` at
  the group, unary or list token and stops, instead of overflowing. Every recursive engine walk (label evaluation and
  predicate evaluation) calls `RuntimeHelpers.EnsureSufficientExecutionStack`, and the engine reports
  the exhausted stack as `CG05008`. The walks that need no recursion do not recurse: shape
  validation, name collection, anchor equalities, `ToString`, structural equality and hashing use an
  explicit stack. This is .NET's form of Neo4j's backstop: .NET cannot catch a stack overflow, so the
  check runs before the descent instead of after the overflow.
- **Hand-built trees.** The planner validates shape, not depth: an undefined operator, a null operand
  or operand list, or a chain with fewer than two operands is `CG05001`.
- **Insertion.** An inserted node takes a conjunction of any length and receives each label once, in
  first-mention order (`:A&A` labels it `A`). Unlike Neo4j's spill to dynamic label records, a node's
  labels and properties share one graph record of at most 8,092 bytes, so the number of distinct
  labels one node can carry is bounded by their encoded size, not by the language; an insertion past
  it fails with the engine's `CG02009`, element too large, and the session stays open (Graph engine
  design, "Planning, execution and bounds").

`GqlLabelChainParserTests` pins 10,000-name chains of every operator in every position, the merge
rule, precedence after flattening, the renderer round trip, 10,000 nested groups on a large stack and
`CG00009` on a small one. The engine's `GqlLabelChainExecutionTests` and Graph.Client's
`GraphLabelChainWireTests` pin execution, insertion, `CG05008` and `CG02009` in process and over
the wire.

## Scalar WHERE expressions (#1140)

The public AST adds GqlUnaryExpression, GqlIsNullExpression, GqlTruthTestExpression,
GqlInExpression, GqlStringPredicateExpression (with its operator enum) and GqlListExpression.
Lists occur only as IN's right operand; their items are scalar expressions. Lists elsewhere
and list-valued parameters belong to #1146. The planner validates every child with an explicit
stack and rejects malformed hand-built shapes with CG05001. Index anchoring and required
unknown-name inference descend only through AND. Neither OR/XOR nor NOT can supply an
anchor or make an unknown label empty the whole plan.

Precedence from weakest to strongest; binary runs associate left, comparisons do not chain:

| Level | Operators |
| --- | --- |
| 1 | OR, XOR (one left-associated level) |
| 2 | AND |
| 3 | NOT |
| 4 | Comparisons, IN, string predicates, followed by optional IS [NOT] NULL/TRUE/FALSE/UNKNOWN |
| 5 | String concatenation `\|\|` |
| 6 | Binary + and - (including + on two strings) |
| 7 | * / % |
| 8 | Unary + and - |
| 9 | Literals, properties, labeled predicates and parentheses |

`NOT a.x = 1 OR a.y = 2 AND a.z = 3` is OR(NOT(=), AND(=, =)).
OR and XOR share a level in the [openGQL grammar, GQL.g4:2062](https://github.com/opengql/grammar/blob/main/GQL.g4#L2062).
Thus `TRUE OR TRUE XOR TRUE` is `(TRUE OR TRUE) XOR TRUE` (FALSE), and
`TRUE XOR TRUE OR TRUE` is `(TRUE XOR TRUE) OR TRUE` (TRUE).
[Neo4j's GQL conformance table, GE07](https://neo4j.com/docs/cypher-manual/current/appendix/gql-conformance/supported-optional/)
records Cypher's different XOR precedence; this parser follows GQL.
Signed numeric literals fold, including -9223372036854775808. In expression context the
shared lexer's left arrow in `a.age<-1` means `a.age < -1`. ISO's `--` always starts
a line comment: `a.x--1` leaves a bare property predicate; write `a.x - -1` to subtract
a negative literal. The pattern-only CG00008 rule is unchanged.

AND, OR, XOR and NOT accept Boolean or NULL. AND stops on FALSE, OR stops on TRUE;
otherwise any NULL makes the result UNKNOWN. UNKNOWN is also accepted as a Boolean literal,
represented by NULL, as in the openGQL grammar's BOOLEAN_LITERAL production (GQL.g4:2974-2978).
XOR is UNKNOWN if any operand is NULL,
otherwise true for an odd number of true operands. WHERE retains only TRUE; an evaluated
non-Boolean operand raises CG02013. IS NULL tests any scalar; IS TRUE/FALSE/UNKNOWN
tests Boolean/NULL, always returning Boolean, inverted by NOT. Comparisons with NULL are
UNKNOWN. Incomparable non-null types are unequal (= false, <> true); their ordering is
UNKNOWN, so NOT does not turn such an ordering into a match.

Arithmetic promotion is a Cohesion extension for the typed API's stored CLR types.
It does not apply to comparisons or IN, which use exact fractions shared with DISTINCT.
The first applicable row selects arithmetic conversions and the result type:

| Operands | Arithmetic result |
| --- | --- |
| Either float or double | Double |
| Otherwise either ulong or decimal | Decimal |
| Otherwise supported integral types | Int64 |

Int64 arithmetic is checked. Int64 / truncates toward zero; % is its corresponding remainder
with the dividend's sign. Every integral remainder by -1 is 0, including Int64.MinValue;
Int64.MinValue / -1 still overflows (PostgreSQL `src/backend/utils/adt/int8.c:584-594`,
relative to C:/Source/repos/postgres/postgres). Decimal stays Decimal; floating arithmetic stays Double and rejects
non-finite results. Division/remainder by zero, overflow, invalid numeric signs, mixed
string/number + and invalid concatenation types carry CG02013. Two strings make + an alias
for `||`, without conversion. Arithmetic with NULL is NULL. Cohesion SQL promotes every
non-integral operand to Decimal; GQL deliberately uses Double for floating operands.

**Standards and spellings.** The [ISO contents preview, sections 19 and 20](https://cdn.standards.iteh.ai/sist-preview/76120/a5695073ca0a485c9929f50970f4c862/ISO-IEC-39075-2024.pdf)
lists comparisons, null predicates and Boolean/numeric/string expressions, with no IN or
string-search predicate. The [openGQL grammar](https://github.com/opengql/grammar/blob/main/GQL.g4),
productions for sections 19.2 and 20.1, also contains neither IN nor STARTS WITH/ENDS WITH/CONTAINS.
These four spellings are therefore recorded as Cohesion compatibility extensions. This checks
the public contents and grammar, not the paywalled normative text. ISO string concatenation
is `||`; string + is the Cypher alias. % is the Cypher spelling of the ISO MOD operation;
the canonical MOD function executes in #1148.

Local Neo4j references, relative to C:/Source/repos/neo4j/neo4j (behavior consulted, no code copied):

- `community/cypher/front-end/parser/v25/parser/src/main/antlr4/org/neo4j/cypher/internal/parser/v25/Cypher25Parser.g4:546-600`
  spells IN and the string predicates. Its separate OR/XOR levels are Cypher-specific;
  GQL's shared level above governs this parser.
- `community/cypher/front-end/expressions/src/main/scala/org/neo4j/cypher/internal/expressions/PredicateExpressions.scala:227-231,269-295`
  models membership and the three string predicates, whose signatures accept any operand types.
- `community/cypher/interpreted-runtime/src/main/scala/org/neo4j/cypher/internal/runtime/interpreted/commands/predicates/Predicate.scala:447-483`
  returns UNKNOWN unless both string-predicate operands are text.
- `community/cypher/interpreted-runtime/src/main/scala/org/neo4j/cypher/internal/runtime/interpreted/commands/predicates/CachedIn.scala:37-58`
  returns FALSE for an empty list. IN otherwise returns TRUE for a matching non-null item;
  with no match, NULL in the searched value or list makes UNKNOWN, otherwise FALSE.
  Thus NULL IN [] is false and 2 IN [1, NULL] is unknown.
- `community/cypher/interpreted-runtime/src/main/scala/org/neo4j/cypher/internal/runtime/interpreted/commands/expressions/ListLiteral.scala:36-45`
  evaluates every list item before returning the list; `CachedIn.scala:38-49` consumes that
  evaluated list before testing membership. Cohesion likewise evaluates the complete list
  before comparing, so `42 IN [42, 1 / 0]` raises CG02013 in either item order.
  A match still wins over NULL: `1 IN [1, NULL]` is TRUE, `1 IN [2, NULL]` is UNKNOWN.
- `community/values/src/main/java/org/neo4j/values/utils/ValueBooleanLogic.java:170-186`
  checks the empty list before the searched NULL value;
  `community/values/src/main/java/org/neo4j/values/utils/InCache.java:94-118` lets a matching
  value win before returning UNKNOWN for a NULL item with no match.

Arithmetic chains have no parser count limit. A long left-deep arithmetic AST can exhaust the
evaluation stack and reports CG05008. NOT, groups and nested IN lists also retain the
CG00009/CG05008 backstops without a fixed language depth limit. GqlWhereExpressionParserTests,
GqlWhereExpressionTests, GqlProfileExecutionTests and GraphClientWireTests cover ASTs, truth
tables, typed numeric promotion, anchors, exclusions, stack failures, exact rows and protocol
1.0 statement failure completion before ResultHeader.

## AOT posture and extension discipline

The package references only `Database.Language`. It uses ordinary typed AST classes and records
and BCL scalar values with no reflection, Regex, runtime discovery, or `Microsoft.Extensions.*`
dependencies; `GqlLabelExpression.ToString` renders GQL text with a type switch. Adding a
clause requires the parser, planner, executor, conformance cases, and this matrix in the same change;
keyword recognition alone never enables it.

## Function declaration and diagnostic delivery (#1103)

Profile.Functions is the executable built-in set, checked by the engine's one function-
coverage guard: every declared name has a passing live case and every live case has a
declared name. GQL declares the aggregates and the core scalar names and aliases in the function table above. OQL removes ABS (already recognized by its
unsupported table), and its parser/planner membership checks read the profile. OQL's
[temporary ODMG exemptions were removed by #1126](../../Assimalign.Cohesion.Database.Documents.Language/docs/DESIGN.md#function-declaration-and-diagnostic-delivery-1103).

The root DatabaseException.Diagnostics carrier preserves the complete parser list at text
factories and session boundaries, including parsed typed requests. DatabaseParseException
retains the existing first-error message and no error-recovery AST executes. Graph's path
request factory shares this contract. Diagnostics remain plain Database.Types data.

## Execution budgets (#1533)

The engine streams internal read bindings and applies deterministic retained-memory and physical-work
budgets. Unordered read LIMIT may stop expansion (D4); final results stay materialized until statement
completion. GraphQueryLimits carries optional lower MemoryBytes and WorkUnits, with positive engine
ceilings of 256 MiB and 1,000,000 units by default. It is an in-process request contract; no grammar,
Execute payload, capability, wire version or disk format changes. See the engine DESIGN for ownership,
CG05020/CG08021 and the preserved 64-hop path limit.

## Explicit transaction text (#1540, D5)

START TRANSACTION, COMMIT and ROLLBACK are standalone ISO statements on Execute, using the
existing session transaction objects. BEGIN [TRANSACTION] is a standalone alias of START TRANSACTION
(#1725, owner decision 2026-10-25); both use Snapshot. Savepoints, transaction options,
statement composition and EXPLAIN of transaction controls remain outside this profile.
A failed statement aborts the transaction; subsequent work is refused with CG04007 until
ROLLBACK. Failed COMMIT rolls back and reports failure, never silently commits. Duplicate START
uses CG04022 and aborts the existing transaction; COMMIT/ROLLBACK without an open transaction
uses CG04023. ExecutePaths refuses controls with CG05001 before any control takes effect.
Disconnect and session teardown roll back. The shared negotiated transaction-status postamble
also resets unfinished transactions before pool reuse; legacy peers are discarded on return.

An idle open wire transaction holds the database writer lock once it has acquired it, until
COMMIT, ROLLBACK, disconnect, pool return or IdleTimeout (30 minutes by default). Keep transactions
short: an idle writer blocks other writers. New clients discard sessions against pre-#1540 servers
that cannot negotiate transaction-status, so those servers are not pooled.

The parser exposes GqlQueryExpression.TransactionCommand (GqlTransactionCommand None/Start/Commit/Rollback).
The server owns the connection's existing transaction, following Neo4j's
community/bolt/src/main/java/org/neo4j/bolt/protocol/common/fsm/transition/ready/CreateTransactionStateTransition.java:63-81
(local Neo4j clone). No code is copied, and no transaction wire message is added.

The optional TRANSACTION spelling follows the local PostgreSQL grammar at
`C:/Source/repos/postgres/postgres/src/backend/parser/gram.y:11462-11469` and
`:11483-11485`. Graph admits only the optional TRANSACTION word: WORK, TRAN, access modes,
isolation levels and deferrability still report CG00002 at the first trailing token.

## Graph diagnostic conventions (#1470)

Exception and diagnostic templates use the project's build-generated `ErrorMessages` resources.
Parameterized templates format with the current culture; abort/cancellation templates used in
normal cleanup are cached. The [Database error-code registry](../../Assimalign.Cohesion.Database/docs/ERROR-CODES.md)
assigns this family its two-letter prefix and categorized five-digit identities.
Existing SQLSTATE meanings, numeric wire error kinds, frame layouts and disk formats are preserved.
Structured diagnostics keep their code outside the message. Compiler diagnostic identifiers and
standard .NET argument guards retain their own contracts.

## Debugger inspection (#1470)

Language debugger views expose stored AST properties and reviewed source-text/location getters on sealed leaves. They omit Stages, SetItems, RemoveItems and Segments materialization, Matches/Predicate list-pattern access, parse Diagnostics sorting and parser Profile initialization. Source text is not included in debugger summaries.

Eligible types follow the [Database conventions](../../Assimalign.Cohesion.Database/docs/CONVENTIONS.md) debugger display/proxy rules. Views are private and read-only; inspection does not change the public API, execution paths, error contracts, storage formats or protocol bytes.

## Deferred engine function names

The default standalone GqlQueryParser retains CG00010 for an unknown call.
The explicit allowCustomFunctions constructor admits ordinary identifier calls as immutable
GqlNamedFunctionCallExpression nodes with generated GraphFunctionName identifiers, copied
arguments, DISTINCT metadata and source spans. GraphQueryRequest.FromGql opts in automatically;
the engine resolves scalar/aggregate kind and arity before any execution. The parser does not
invoke a callback or receive an engine catalog. Native enum-based AST construction remains valid.
COUNT is still the only star form; CASE/COALESCE/NULLIF/CAST keep their grammar and laziness,
and window/HAVING and other unsupported forms remain refused. Invocation-shaped OVER is
recognized as WINDOW OVER (CK00001), including inside a deferred custom call; outside an
invocation, over remains a legal variable, label, alias and property name. The builder rejects
OVER as a function name through the same syntax-only admission probe.

This follows the separation of signature resolution and borrowed execution values in Neo4j
community/procedure/src/main/java/org/neo4j/procedure/impl/ProcedureRegistry.java:127-201 and
community/kernel/src/main/java/org/neo4j/kernel/api/procedure/CallableUserFunction.java:27-37.

Engine request parsing admits unknown named calls for engine-local catalog binding. Unknown names
retain CG00010 but ordinary requests fail during execution (wire ExecutionFailure, without
StatementParseFailed); standalone parser diagnostics are unchanged. See DIALECT.md.

ORDER BY alias result kinds resolve projection expressions in the saved incoming scope,
including Boolean and list aliases referenced beneath shadowing collection iterators (#1736).

Reduce static result kinds use the same accumulator closure: the union includes the seed and
body feedback, keeps Boolean alternatives, and lets unknown kinds retain runtime checks.
NULL-only branches add no non-NULL kind. Known incompatible predicate/source kinds fail
CG02013/CG02015 even for empty sources. Graph bounds this analysis separately from execution
with a cancellable 65,536-visit/step statement planning cap (CG05026), including EXPLAIN.
Invariant source/seed facts and completed closure depths are memoized within their lexical scope.
