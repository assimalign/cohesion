# Assimalign.Cohesion.Database.Sql.Language — Design

## WITH RECURSIVE binding (#1719, SQ-R1)

WITH RECURSIVE declares visibility of every name throughout its WITH list. Acyclic
entries bind in dependency order and keep ordinary reachable-only materialization.
Unqualified self-references identify recursive entries; schema-qualified names still
resolve in the catalog. Mutual recursion reports CK00001. A recursive entry requires
an anchor UNION [ALL] recursive term and exactly one self-reference in the latter.
The anchor fixes output names, width, types, modifiers, domains and collations before
the recursive FROM binds. A complete column list renames the output. Unknown anchor
NULL/string output becomes text. Overall UNION type/modifier and collation changes
report CS02066 and CS02067 with cast/COLLATE hints; widths report CS02065.

The recursive reference cannot appear in the anchor, in scalar/IN/EXISTS/quantified
subqueries, on a nullable outer-join side, under INTERSECT ALL, or on either side of
EXCEPT ALL or the right of EXCEPT DISTINCT. INTERSECT DISTINCT, the left of EXCEPT
DISTINCT, preserved outer-join sides and derived FROM queries follow PostgreSQL's
allowed forms. Aggregates at the level containing the reference are invalid;
independent subquery aggregates remain allowed. Outer-only subquery aggregates
owned by the recursive level also report CS01061, including projection, HAVING and
ORDER BY subqueries over grouped recursive inputs (PostgreSQL
`src/backend/parser/parse_agg.c:646-690,1327-1334`). Top-level recursive UNION ORDER BY,
LIMIT/FETCH, OFFSET and FOR UPDATE/SHARE use CK00001. SEARCH/CYCLE, CREATE RECURSIVE
VIEW, nested WITH and modifying CTE bodies remain CK00001.

Valid recursive definitions bind all entries and the consuming statement, then
planning, EXPLAIN and execution return CK00001 until SQ-R2, even for an unused
recursive definition. No working-table operator or depth limit is introduced here.
Binding defers count/seek and constant-fold evaluation once recursion is identified.
CREATE VIEW with a recursive query body freezes ordinary anchor metadata without
executing it; cached and reopened view use propagates the same planning refusal.
No catalog, storage or wire format changes are needed.

IsRecursiveScope records the list modifier on each AST definition, independently
of whether it self-references. Binding retains the existing case-insensitive name
rules, requires complete output-name lists and conservatively makes recursive
columns nullable. Independent ready definitions retain declaration-order priority.
Complete FOR UPDATE, FOR NO KEY UPDATE, FOR SHARE and FOR KEY SHARE strengths
are recognized only in the SELECT tail and refused with CK00001, including in
subqueries and CTE bodies (PostgreSQL `src/backend/parser/gram.y:13119-13176,13803-13840`).
Qualified identifiers and implicit aliases retain their ordinary parsing; incomplete
strengths remain syntax errors. FOR is in the recognized-unsupported vocabulary and
keyword-disposition corpus, without becoming a reserved identifier.

Plain WITH before MERGE shares SELECT CTE binding and materialization. WITH RECURSIVE
before MERGE refuses at the RECURSIVE token with CK00001, even for acyclic lists,
following PostgreSQL `src/backend/parser/parse_merge.c:124-129`.

References re-derived from the local PostgreSQL clone: src/backend/parser/parse_cte.c:
345-400 (output checks and hints), 580-632 (unknown anchor output), 648-725 and 863-906
(dependencies), 915-1019 (form/tails), 1027-1199 (references and joins), 1209-1277
(set operands); parse_collate.c:807-860 (implicit default and conflicts);
parse_agg.c:1327-1334 (aggregates). No reference code is copied.

| Code | Condition | PostgreSQL SQLSTATE |
| --- | --- | --- |
| CS01054 | Recursive definition lacks a top-level anchor UNION [ALL] recursive term | 42P19 |
| CS01055 | Recursive reference in the anchor | 42P19 |
| CS01056 | Recursive reference inside a scalar, IN, quantified or EXISTS subquery | 42P19 |
| CS01057 | Recursive reference on the nullable side of an outer join | 42P19 |
| CS01058 | Recursive reference under INTERSECT ALL | 42P19 |
| CS01059 | Recursive reference in a prohibited EXCEPT operand | 42P19 |
| CS01060 | Recursive term has more than one self-reference | 42P19 |
| CS01061 | Aggregate at the query level containing a recursive reference | 42P19 |
| CS02065 | Recursive anchor, recursive output or declared column-list width mismatch | 42601 |
| CS02066 | Recursive UNION changes the anchor type, domain or type modifiers; cast hint | 42804 |
| CS02067 | Recursive UNION changes or conflicts with the anchor collation; COLLATE hint | 42P21 |

## Joined-DML statement memory (#1715, #1751)

DIALECT.md records CS05067 before writes when retained MERGE, UPDATE FROM or DELETE USING
inputs, associations or selected writes exceed StatementMemoryBudget. Hash builds share that
grant without a second charge for aliased payload. Syntax, AST and persisted formats are unchanged;
the engine's joined-DML memory conformance cases cover refusal and under-budget execution.

## MERGE execution (#1715, SQ-M2/SQ-M3)

DIALECT.md records ordered executable actions, BY SOURCE, old/new target RETURNING,
merge_action(), repeated-target CS05036 and lazy action-owned subqueries. The existing AST,
renderer and grammar are unchanged. Live clause conformance executes MERGE rather than
measuring only EXPLAIN; SQL's design records transaction admission, ReadCommitted target
rechecks and mixed-action final-state foreign keys. The engine and InMemory/TCP corpora cover
these semantics without changing the syntax or advertised clause count.
The final-state corpus also checks old parent keys of directly updated rows removed by a
cascade and immediate uniqueness before FK or SET action constraint failures, including
NOT NULL, CHECK, domain and identity. Retained failures skip parent-row locking, preserving
the statement's diagnostic when an earlier matched parent is stale. Actual target-version conflicts carry
CS04064; shared RI cascade snapshot and matched-parent version checks, host-ended transactions
and abandoned lock owners keep their ordinary uncoded abort. The review corpus pins both RI
checks alongside the existing coded direct-target conflicts.
The recorded two-session cascade corpus checks transaction-duration old-parent locks when a
direct child update moves into a cascade, for both commit and rollback.

## ALTER TYPE domain rendering (#1447)

SqlColumnDefinition retains the parser's decoded type name separately from its type spelling.
ALTER TYPE renders this structured name with identifier escaping, so a decoded domain name
beginning and ending in quote characters cannot be confused with SQL delimiters. Parser and
direct-AST round trips cover both forms. DIALECT.md records TYPE's assignment-default casts,
narrowing rewrites and persisted CHECK rules; the engine's live conformance corpus exercises them.

## TRUNCATE (#1716, SQ-T1)

SqlTruncateExpression stores an immutable ordered list of SqlTruncateTable targets, each with
its ONLY modifier, plus effective restart/cascade flags. TABLE is optional; CONTINUE IDENTITY
and RESTRICT are the defaults. Canonical rendering supplies TABLE and both effective policies.
TRUNCATE adds one keyword and one advertised clause; RESTART/CONTINUE/IDENTITY remain contextual.
Completeness and live conformance cases cover both commands.
The engine's snapshot, closure, identity and reclamation contract is recorded in DIALECT.md.
No public construction bridge or interface is added. Truncate is appended to SqlQueryCommandType;
the existing Begin, Commit and Rollback values remain 9, 10 and 11.

## CREATE TABLE AS (#1716, SQ-T2)

SqlCreateTableAsExpression is a sealed CREATE leaf with an immutable optional complete output-name
list, a query body and effective WithData/IfNotExists flags. The definition-list parser dispatches
to CTAS at AS, including after a parenthesized name list. It shares SELECT/WITH/set grammar,
capability checks and nesting measurement. Canonical rendering supplies the effective data policy.
CREATE TABLE AS adds one advertised clause beside CREATE TABLE, with no new keyword or command
enum value. DIALECT.md specifies metadata, transaction refusal, publication and error semantics;
parser/profile and live engine conformance cases advance together.
Output metadata retains domain defaults and their resolved call vectors. WITH DATA keeps normal
indexed access planning; WITH NO DATA alone suppresses seek-bound evaluation. The complete name
list follows CREATE VIEW's Cohesion rule, as DIALECT.md records.
The engine reports copied-row completion counts, shares the statement grant between retained
source output and batches, and handles losing name races as duplicate/no-op. Ordinary snapshot
isolation follows the builder's single commit: snapshots before it see no rows; snapshots after it,
including those captured before catalog publication, see every row once the live catalog can bind
the table. Admission proceeds normally. DIALECT.md also records the whole-build metadata lease
and statement epoch; syntax and format 26 remain unchanged.

## Repeated subquery grouping keys (#1718)

DIALECT.md records structural matching of complete scalar-subquery, EXISTS and quantified
group keys across SELECT, HAVING and ORDER BY, including identical correlations. Operators,
quantifiers, negation, scalar operands and query bodies remain significant; IN keys also compare
bodies. This is an engine binding rule; syntax and persisted representations are unchanged.

## Joined DML planning and execution (#1714, SQ-UF2)

SqlUpdateExpression.From and SqlDeleteExpression.Using retain comma items and existing relation/join
variants without adding a public construction bridge. Shared parsing and canonical source rendering
preserve SELECT behavior and DML round trips, including WITH, derived bodies and RETURNING. The
capability scan admits read-source queries while keeping SET/RETURNING subquery exclusions.
Visible source OLD/NEW names mask pseudo-relations. The profile now advertises UPDATE FROM and
DELETE USING; DIALECT.md records pre-write CS05068 and physical-target DELETE deduplication.
Joined execution uses target-outer predicate pushdown/costing, one materialization per CTE, and
statement unwind before WITH/subquery wrappers. Both project overviews reflect executable support.
DELETE and MERGE's top-level USING source positions recognize keyword-spelled CTE
references, including over, partition, window and natural in definitions and
qualifiers, while WITH RECURSIVE now binds under #1719. Canonical rendering delimits ordinary OLD/NEW column qualifiers
so a quoted identifier cannot become a RETURNING pseudo-relation on reparse.
Bare RETURNING * follows PostgreSQL's visible namespace expansion: target columns, then sources
in FROM/USING order with USING merges preserved and duplicate output names allowed. The engine
retains visible output ordinals separately from physical association slots; plain DML is unchanged.
Equality-driven steps follow written source order; DIALECT.md documents the dependence and the
source-order workaround pending general join-order search in #1629. No reordering is introduced.

## Cascade progress and deadlock diagnostics (w11 review)

DIALECT.md records the per-statement bound of three cascade lock refusals: two releases,
then normal queued waits retaining closure grants and deadlock detection. Hard deadlocks
carry CS04065 mapped to 40P01; explicit victim rollback remains caller-managed under
statement atomicity. Grammar, diagnostic transport and disk formats are unchanged.

## Parent replacement visibility (#1265)

DIALECT.md records retryable FK probes by replacement ObjectId across renames. Empty input,
MATCH SIMPLE null keys and input-satisfied self-keys require no parent probe; read-committed
ON CONFLICT uses refreshed probe visibility. Engine conformance cases exercise empty UPDATE,
empty INSERT SELECT and all-null FK inserts across a retained parent rewrite. Grammar, AST,
diagnostic identities and format 26 are unchanged.

## Bounded SELECT evaluation (#1523)

DIALECT.md records early termination across stored/system, grouped, correlated,
join and set output paths, including skipped projection/HAVING errors and calls.
It also specifies input-order projection/key evaluation, the full-sort fallback
for independent volatile outputs, and qualifying-row collation preservation.
These are engine execution rules; grammar and public syntax are unchanged.

## Result encoding failures (#1651)

DIALECT records CS05045's result encoding contract. Writing statements prepare results before
commit, with DML failures rolling back only the statement. Read-only statements encode during
delivery; late failures end partial results without invalidating the connection or explicit
transaction. Both paths enforce only the per-frame header/row limit, with no aggregate cap
(size refusals correspond to SQLSTATE 54000). Transport failure remains terminal.
Parser and AST shapes are unchanged.

## Key declaration semantics (#1192)

The engine rejects repeated child or referenced key columns with CS01019; rejecting
repeated FOREIGN KEY child columns is stricter than PostgreSQL. Existing-table CREATE TABLE
IF NOT EXISTS skips key declaration validation and preserves the table. DIALECT.md records
these execution rules; parser shapes and advertised clauses are unchanged.

## Less-than followed by minus (#1647)

The shared profile leaves `RecognizeLeftArrow` disabled. `a<-1` and `a <- 1` parse as `a < -1`;
`a<--1` starts a line comment after `<` and reports the existing missing-operand syntax error.
`SqlLeftArrowConformanceTests` pins the comparison tree and error. See Database.Language DESIGN
for the PostgreSQL scanner reference and configuration contract.

## Delimited identifiers (#1235)

The shared lexer keeps `"a""b"` as one identifier token; CurrentIdentifierText decodes it to
`a"b` for every name position. Expression rendering doubles embedded quotes in ordinary and
function identifiers. Lexical completeness checks distinguish a closing quote from a final
escaped quote, so `"a""` is CS00003. Parser conformance, identifier-value and renderer round-trip
tests pin the behavior. See Database.Language DESIGN for the PostgreSQL scanner reference.
Simple column-reference spans retain the complete raw token, including delimiters and doubled
quotes. Persisted-view binding edits use those source spans, so canonical expansion replaces
the entire reference rather than leaving a suffix of the quoted name behind.

Persisted view text written by the base engine can contain adjacent delimited tokens without
whitespace: `"a""x"` formerly parsed as column `a` with alias `x`, and now names the single
identifier `a"x`. A base-created view with that stored spelling can therefore rebind differently
or fail after reopen. The unambiguous old form `"a" "x"` keeps its meaning. There is no stored-text
migration or alternate lexer; recreate an affected view with explicit whitespace or `AS`.

## MERGE syntax and binding (#1715, SQ-M1)

SqlMergeExpression retains a target, one USING relation/join item, ON, ordered
SqlMergeWhenClause values and optional RETURNING. Each clause retains its complete source span
from WHEN through its action, so CS05071 locates the unreachable clause. Match/action enums are separate;
BY TARGET's explicit spelling survives canonical round trips. MERGE adds one keyword
and one advertised clause; MATCHED, SOURCE, TARGET, DO, NOTHING and merge_action remain
contextual. The latter uses the existing function-call AST and binds only in MERGE
RETURNING, where Sql maps it to a text slot. It is not a standalone executable built-in.
Grammar requires the action set appropriate to the match kind. Unreachable clauses,
scopes, column/type validation and planning belong to Sql. DIALECT records the complete
binding, executable action, target recheck and final-state FK contracts. No persisted format changes.
USING shares UPDATE FROM's stored/system/view, aliased derived-query and supported join
sources. At the top level a source CROSS JOIN leaves ON for MERGE; ordinary FROM parsing
keeps its existing CROSS JOIN ON refusal. VALUES relations, table functions, LATERAL,
NATURAL JOIN and aliasless derived queries retain the existing source diagnostic codes.

## INSERT conflict clauses (#1118)

SqlOnConflictClause retains inference kind, column set or constraint name, action,
assignments and optional filter. The parser supplies its EXCLUDED relation reference to
keep construction inside this assembly. ON CONFLICT adds one advertised clause; no keyword
or unsupported-vocabulary row changes. INSERT OR IGNORE/REPLACE and ON DUPLICATE KEY UPDATE
are positional capability refusals (REPLACE uses its existing function token). Inference
expressions/options, partial WHERE and DO SELECT retain named CK00001. Preflight resets
INSERT VALUES/FROM state at arbitration and names SET/WHERE subqueries ON CONFLICT DO UPDATE,
including after WITH-prefixed INSERT SELECT. RETURNING ends that scope and refuses
the EXCLUDED pseudo-relation; a visible target table or alias really named excluded masks
that refusal, as OLD/NEW do (PostgreSQL src/backend/parser/analyze.c:1269).
DIALECT specifies execution and errors.

## CREATE modifier dispatch (#1124 review)

CREATE examines the object word after modifiers before choosing the view parser.
Excluded TEMP/TEMPORARY TABLE and OR REPLACE FUNCTION constructs keep their own
CK00001 names. View input references are serialized from their engine bindings;
ORDER BY output aliases remain output references. DIALECT.md specifies frozen view
metadata and isolated bind failures.

## Parameters (#1102)

SQL follows the [shared parameter contract](../../Assimalign.Cohesion.Database.Language/docs/DESIGN.md#parameter-contract-1102).
The parser retains sigils in SqlParameterExpression. It links Execution's stateless
name check, with no Execution assembly dependency. Numeric names use ASCII digits only;
Unicode numeric spellings such as `$١` and `$1١` report CS00003 at the whole reference.
Malformed names report CS00003; operand `?` and `:name` use the shared supported-form message.
Trailing `?` keeps the lexical generic error. Adjacent host names in operand position bypass the
clause/capability scan even when they spell keywords such as `:union`, `:like` and `:select`; syntax parsing
reports the whole reference once. DIALECT.md and ParameterConformanceTests measure these forms.


## Fixed-length character declarations (#1437)

CHAR and CHARACTER resolve to DatabaseType.Character, distinct from VARCHAR/TEXT (String).
The parser supplies length one for bare CHAR, and explicit lengths reuse the existing shared
SqlTypeModifiers rule in every declaration and CAST. CS00005 and the common length ceiling remain
unchanged. The runtime owns padding, trimming casts and comparison; DIALECT.md specifies their
PostgreSQL anchors and the dialect differences. Schema and SDK normalization share this identity
in the [current canonical document](../../Assimalign.Cohesion.Database.Sql.Schema/docs/DESIGN.md#fixed-length-schema-columns-1437).

The SQL front-end (area architecture: [docs/resources/Database/DESIGN.md](../../../../docs/resources/Database/DESIGN.md)
§3.3). The dialect is deliberately fundamental — basic SQL, declared precisely —
and structured to be extended without churning what exists (open/closed).

## NULL placement (#1112)

SqlOrderByColumn retains SqlNullPlacement.Default/First/Last independently of direction.
The shared rule serves query tails and aggregate inputs, using contextual lookahead so
NULLS, FIRST and LAST stay unreserved. Rendering emits explicit placement verbatim in
canonical uppercase and never supplies an omitted modifier. The tree dump and structural
query/call comparisons include placement, preserving persisted view round trips.
No profile clause or keyword is added. DIALECT.md specifies syntax, defaults and errors.

## Aggregate modifiers and string aggregation (#1115)

FILTER and Boolean/statistical aggregates (#1720, SQ-H1) extend this representation:
SqlFunctionCallExpression.Filter retains the optional condition, including depth and source
extent. Rendering, semantic child walks, aggregate-level correlation and persisted equality
include it. FILTER adds one named clause and nine function names join the executable profile.
The profile has 59 declared clauses, or 64 including the five documented exclusions.
Window aggregates and window calls in FILTER retain CK00001. The catalog owns aggregate
admission (CS05072 for FILTER on scalar calls); a same-scope aggregate in the condition is
CS05073. Child queries bind independently. See DIALECT.md for types, NULLs and rounding.

SqlFunctionCallExpression retains a SqlSetQuantifier and ordered input keys for every call;
the engine catalog decides which calls are aggregates. Depth measurement, rendering and
structural equality include the keys. LISTAGG WITHIN GROUP normalizes onto the same ordered
input shape, with DISTINCT accepted. ON OVERFLOW is one contextual capability refusal,
without a vocabulary entry. Explicit ALL is rendered, while execution treats it as default.
No named clause is added; the executable function profile adds STRING_AGG and LISTAGG.
The AST retains HasWithinGroupOrdering so ordinary quoted LISTAGG calls preserve their
ordering syntax through rendering and remain distinguishable for catalog-aware admission.
Special-form names render as quoted ordinary calls when a quantifier or ordering is present;
the standard EXTRACT/POSITION and niladic arms apply only without those modifiers.

Boolean search conditions (#1190) use the existing AST. Engine planning and runtime
reject non-Boolean predicates with CS02003 (42804), including upsert WHERE; NULL
retains three-valued logic. Every CASE result and COALESCE alternative in a Boolean position
must qualify, including unreachable alternatives. Valid Boolean input string literals coerce
in these positions; text columns and parameters keep their types. Non-Boolean table/domain
CHECK roots use the same CS02003 / 42804 code. The live clause corpus pins these errors.

FROM-less SELECT (#1373) retains the existing AST and grammar. The engine now uses
one implicit row for every such query, including views and nested queries; DIALECT.md
specifies filtering, aggregation and wildcard refusal. No profile count changes.

## Executable functions and vocabulary (#1103)

Column DEFAULT uses the restricted comparison/distinct expression grammar, leaving following column constraints
unconsumed (PostgreSQL gram.y:4032-4037). Parentheses, CAST and scalar calls execute; the
engine enforces variable-free definitions and volatility. NO and ACTION are contextual words.
The five delete actions are retained in the AST; omitted references use NoAction (#1121).
SET column lists retain CK00001. The profile and live clause corpus add exactly three clauses.

The #1120 profile executes all 42 T1 scalar names, including bare datetime values.
SUBSTRING/TRIM/POSITION normalize onto calls; NULLIF/EXTRACT remain evaluator forms.
Canonical rendering preserves the standard separators and niladic syntax. Precision
arguments retain a reusable CK00001 rejection; malformed forms use CS00003.
Function tokens share identifier/qualifier parsing, so additions preserve ordinary names.

The profile's Functions list contains only executable built-ins, including the CAST special
form. Non-executing window functions remain in SqlUnsupportedVocabulary and report named CK00001 diagnostics. Bare datetime forms execute and cannot bind unquoted columns.
The engine's single function-coverage guard requires a passing live case for every declaration.

`SqlLanguageProfile.RecognizedUnsupportedVocabulary` exposes a read-only snapshot of spellings
for identifier rendering and unknown-function classification. The internal position/construct
table stays internal. There is no shipped-to-shipped InternalsVisibleTo grant to reuse, so this
is the issue's approved public-profile option; no visibility grant or public internal type is added.

## DML qualifiers (#1106)

The UPDATE and DELETE dialect rows specify the same target qualifier rule as SELECT:
an alias hides the base relation name, and unknown qualifiers fail with located CS01015
before any mutation. The engine supplies relation bindings; parser shapes are unchanged. #1113 adds correlated and uncorrelated WHERE subqueries; the existing
CK00001 refusal is retained for UPDATE SET. PostgreSQL `parse_relation.c:3759`
is the reference for the hidden-name rule.

## System relation joins (#1106)

The FROM join-tree grammar accepts stored tables and registered
INFORMATION_SCHEMA/COHESION_SCHEMA sources in either position. No clause is added;
read-only diagnostics remain. INNER, LEFT and CROSS compose across N inputs (#1109). Engine binding owns source
resolution and one lazy statement catalog capture, including child plans. DIALECT.md
and the live JOIN conformance case cover system-to-system joins.

## EXPLAIN (#1104)

`SqlExplainExpression` wraps a SELECT, INSERT, UPDATE or DELETE tree, retains the source text,
and reports `SqlQueryCommandType.Explain`. The parser handles the prefix once, then uses the
ordinary statement parser and terminator check. Capability scanning classifies the wrapped
statement, so supported subqueries remain supported and excluded UPDATE SET subqueries stay
excluded. DDL, transactions, nested EXPLAIN and ANALYZE receive `CK00001`. The wrapped
statement's nesting measure remains subject to the engine's existing limit. SqlClauses.Explain,
the statement corpus and the live conformance case change together; DIALECT.md is the contract.
The engine's deferred-provisioning admission walks the wrapped statement before binding, just
as it does for an ordinary data statement; an EXPLAIN cannot bypass a pending table reservation.

## Quantified comparison AST (#1718)

SqlQuantifiedComparisonExpression and SqlComparisonQuantifier retain the written
scalar operator, ANY/SOME/ALL spelling and query body. The parser admits subquery
operands after all six comparisons (including !=), preserves completed-sublink
precedence, and uses the existing subquery and expression depth accounting.
The query-operand grammar and SOME alias follow PostgreSQL
src/backend/parser/gram.y:15677-15688,17022-17024.
Rendering and tree dumps retain the whole body, including set operations.
SqlClauses.QuantifiedComparison adds one measured profile clause beside Subquery.
ANY/SOME remain contextual words in the recognized vocabulary for array refusals;
their existing canonical identifier quoting stays stable. Row values retain the
existing syntax refusal and excluded subquery positions keep CK00001. DIALECT.md
specifies truth tables, comparison rules and materialization. No format changes.

## Derived relation AST (#1113, part b)

The public `SqlRelationReference` variant set has sealed named-table and derived
query leaves; DML/DDL targets keep `SqlTableReference`. FROM and JOIN retain the
variant directly. Derived parsing uses the existing subquery and expression depth
budgets, requires an alias, and preserves an optional column-name list. The engine
checks full output-name width and rejects blank names with CS05013 before execution.
Rendering and query depth visit every body. Derived-name classification waits for
catalog binding: only actual sibling references require LATERAL, enclosing-query
references resolve through the lexical scope chain, and unknown/hidden names retain CS01015.
Correlated and uncorrelated UPDATE/DELETE WHERE children use ordinary subquery grammar;
UPDATE SET and lateral references remain explicit capability refusals.
Outer ordering binds derived outputs, including projection aliases and ordinals, with
the same per-key NULL placement as stored inputs (#1112). Ordering inside derived
bodies executes, including over nested derived inputs. Set-operation bodies execute through the query-body variant (#1114).

## Identity grammar (#1123)

SqlColumnDefinition.Identity retains SqlIdentityDefinition (ALWAYS/BY DEFAULT, signed start,
increment). Canonical identity syntax and the SQL Server IDENTITY(seed, increment) lexical
alias share a contextual parser; no identity word becomes globally reserved. Capability scanning
skips identity option parentheses so START WITH cannot masquerade as a CTE. The type grammar's
shared rejection rule keeps SERIAL/BIGSERIAL/SMALLSERIAL as CK00001 with an identity suggestion.
SqlClauses.Identity and the parser and live conformance corpora advance together; DIALECT.md
defines allocation, exclusions and direction-dependent generator bounds. Explicit NULL conflicts
with NOT NULL, PRIMARY KEY or IDENTITY through the common CREATE/ADD/ALTER column parser (`CS00003`, naming the
column); CREATE also checks table primary keys after reading all definitions. This follows
PostgreSQL `src/backend/parser/parse_utilcmd.c:873`. INSERT target parsing leaves a bare
OVERRIDING clause token unconsumed while explicit AS aliases retain their lexical meaning.
SERIAL spellings live in the shared rejected-type table and cannot declare domains.
Sql.Catalog has its own SQL-independent facet.

## RIGHT and parenthesized join AST (#1525)

The profile admits RIGHT and FULL [OUTER] JOIN with ON or ordered USING columns. FULL execution uses a bounded hash operator; unsupported physical conditions fail engine binding with CS05070.
SqlJoinedTableReference preserves an unaliased parenthesized item and its constituent
namespaces. A group alias lowers to SqlDerivedTableReference over SELECT *, hiding the
constituents and retaining optional complete column-alias lists and the existing 32-level derived binding limit. Parser parenthesis guards, expression
depth, canonical rendering and the conformance corpus cover both forms. The engine keeps
written ordinals and RIGHT USING's coerced right representative while normalizing physical
children to LEFT. Direct RIGHT/FULL/grouped DML sources fail CS05069 at binding; derived SELECT
sources can contain RIGHT or FULL. FULL USING coalesces common-typed values from both inputs; qualified inputs are nullable and merged nullability conservatively combines the original inputs. No clause, keyword, format or protocol count changes.

Relation lookahead checks the token after each leading query wrapper's matching close.
An alias there starts a derived first relation inside a joined group; SELECT itself and
query continuations retain the query grammar. This follows PostgreSQL
src/backend/parser/gram.y:13938-13965,14000-14022 and applies to INNER/LEFT/RIGHT/FULL/CROSS,
including nested groups and the derived SELECT introduced by canonical group-alias rendering.

## Design intent

Decision 49a adds `SqlAlterColumnAction`, a full column-definition action:
`ALTER TABLE t ALTER COLUMN c INT NOT NULL`. It reuses column parsing, including bounds,
DEFAULT and collation, and the executor checks which changes preserve storage representation.
The public sealed SqlAlterColumnTypeAction retains the PostgreSQL TYPE/SET DATA TYPE target
and original-row USING expression (#1447). The public SqlExpressionRenderer.Render(SqlAlterTableExpression)
overload emits ALTER COLUMN ... TYPE with optional COLLATE/USING and quotes decoded domain
names when required, preserving spaces, embedded quotes and keyword names on reparse.
These are public API additions; no public interface is added. This adds no keyword or clause count: TYPE and DATA are contextual and USING
already exists. Separate typed actions
for SET/DROP DEFAULT and SET/DROP NOT NULL lower onto the existing column replacement path
(#1122); RENAME TO and RENAME [COLUMN] have their own immutable action leaves. RENAME and TO
remain contextual, and each form uses the ordinary complete-statement terminator check. Lookahead
copies the token lexer without changing parser bookkeeping. The action changes no clause count.
The SqlAlterAction constructor is private protected, closing its leaves to this assembly. Only the
named excluded forms use CK00001; unknown and incomplete actions use CS00003, including a bare
SET or ALTER c SET/DROP. SET SCHEMA is classified only when both words are present.

Parse the declared dialect into a stable, fully-typed AST that planners, catalogs,
tooling, and the SDK schema compiler can rely on. "Declared" is the operative word:
[DIALECT.md](DIALECT.md) is a contract, not aspiration — the parser, the matrix,
and the conformance corpus change together, and anything outside the matrix fails
loudly instead of half-parsing. `SqlLanguageProfile.Instance` is the executable
contract: it owns the SQL keyword/function tables and opts into only the clauses
the parser implements today. Recognized clauses outside that set produce the shared
`CK00001` model-specific diagnostic; `CS00002` remains the unknown-command diagnostic.

## Why-this-not-that decisions

CREATE/DROP SCHEMA (#1030) have dedicated immutable expression leaves and clause gates;
SCHEMA dispatch precedes the historical optional-TABLE fallback. SCHEMA and AUTHORIZATION
match with IsWord and never enter keyword/unsupported-vocabulary tables, preserving persisted
identifier quoting. AUTHORIZATION and nested schema elements are precisely rejected because
SQL principals and atomic multi-statement DDL are unavailable. DROP defaults to RESTRICT;
its CASCADE gate is DropSchema, independent of the referential Cascade clause. DIALECT.md
and both the completeness and live execution corpora define the supported subset. The shared
stateless SqlNamespaceNames.Default supplies dbo to runtime provisioning and SDK migration SQL.

SqlQueryBodyExpression is the closed base for SELECT and SqlSetOperationExpression (#1114),
with CommonTables and result-tail fields shared across both. All six query-bearing slots take
the base, and Render has a base-typed overload that persisted views use (#1124). Set operations remain a distinct
root so scalar-expression carriers and SDK migration validation keep refusing set-root injections.
A two-rung query parser follows PostgreSQL gram.y:845-846,13103-13115, keeping TOP at SELECT
head and parsing the result tail after the operator ladder. Parentheses use the existing depth
and stack guards; each left-deep set link counts as a tree level. Renderer precedence preserves
right operands at equal precedence and operands with windows. The same scalar-subquery funnel
admits set bodies once per subquery level. Redundant IN query parentheses now mean membership.
Query lookahead checks each leading wrapper at its matching close, retaining scalar value
continuations (PostgreSQL gram.y:15645,15899,15916). Refused CORRESPONDING and parenthesized
WITH skip to the caller's closing parenthesis and emit one diagnostic. Expression and set-shape
fuzz require every generated input to parse. Query lookahead stops at the parenthesis budget and retains the caller's query or value grammar,
so hostile value parentheses keep their exact diagnostic span without repeated unbounded scans.
UNION/INTERSECT/EXCEPT stay keywords; contextual CORRESPONDING adds no vocabulary row or
format bump. DIALECT.md and the conformance corpora pin these choices.

Pagination (#1107) normalizes LIMIT, FETCH and the D1 TOP lexical alias into the existing
Limit/Offset fields and count validator, with a nullable SqlLimitSyntaxKind on SELECT
so tools and conformance distinguish the syntax family. No extra plan or executor is needed.
FIRST stays contextual. Unparenthesized TOP consumes one integer or parameter, while
TOP (expression) uses the ordinary pagination subquery ban. Canonical output uses
LIMIT before OFFSET, FETCH FIRST n ROWS ONLY and TOP (n), preserving the syntax kind
rather than source trivia. FETCH consumes a primary expression or signed numeric constant,
following PostgreSQL src/backend/parser/gram.y:13674-13696; compound counts require
parentheses, which canonical rendering restores. Renderer fuzz roots and WITH bodies
generate all families, including parenthesized compound FETCH counts. INSERT, UPDATE and
DELETE TOP are recognized DML TOP exclusions with CK00001.
PERCENT and TIES are contextual unsupported entries; their identifier quoting requires
the engine format-19 gate. PostgreSQL gram.y:13537-13658 supplies the offset/fetch grammar.

WITH (#1116, #1719) uses an ordered CommonTables list on each SELECT/INSERT/
UPDATE/DELETE/MERGE root and a sealed SqlCommonTableExpression leaf. This preserves the
existing command dispatch and exposes bodies without a wrapper statement variant.
INSERT INTO ... WITH attaches the list to its SELECT source. Prefix parsing is
separate from ordinary subquery parsing, so bodies never become SUBQUERY in WITH.
The capability scan continues through the complete prefix and rejects nested,
modifying, materialization-hint and SEARCH/CYCLE forms precisely. PostgreSQL
src/backend/parser/parse_cte.c:214-229 supplies declaration-order visibility.

FROM exposes one SqlFromItem join tree per comma item (#1109), preserving JOIN precedence
and ON scopes without recovering grouping from flattened text. SqlJoinClause retains ordered
UsingColumns separately from ON. The capability pre-scan skips advertised clauses and reports
the first unadvertised construct, so USING cannot mask NATURAL, WINDOW or OVER.
The renderer and nesting measure traverse every item and condition; catalog-aware correlation binding belongs to the engine.
The engine implements written-order steps; DIALECT.md defines LEFT null extension and the
common-typed merged expressions and modifier clearing for USING. Qualified wildcards keep
CK00001. ORDER BY aliases and ordinals count visible wildcard output after USING merges.


- **Recursive descent, hand-written** — over a parser generator. The dialect is
  small, error tolerance matters more than grammar elegance, the AST is the
  product, and AOT rules out runtime-generated parsers. Keyword dispatch at the
  statement level and a precedence ladder for expressions make extension points
  obvious: a new statement kind is a new branch + partial file; a new operator is
  a new rung.
- **Error-tolerant, total parsing.** Malformed input yields a statement with
  diagnostics — never an exception. Hostile inputs are part of the conformance
  corpus. This is what lets tooling (editors, the schema compiler) reuse the
  parser on incomplete text.
- **Total parsing never means silent parsing (#1068).** A recursive-descent branch
  returns at the first token it does not understand, so `ParseCore` requires the
  statement to end at the end of the text or at one terminating `;`, for every
  statement kind. Anything left over is `CS00003` at the first unconsumed token.
  Required tokens (closing `)`, `THEN`, `END`, `BY`, `SET`, `=`, names) are
  expected, not skipped when absent. Error recovery may still substitute a
  placeholder node, such as the NULL literal for a missing expression or the `?`
  table name, but only together with an error diagnostic, so a placeholder never
  executes. Recovery leaves the offending token in place for the caller, and every
  token-anchored syntax diagnostic, the leftover check included, skips a position
  an earlier error already covers, so a mistake is reported once per position.
  Before parsing, `ParseCore` scans the tokens for lexical errors the shared lexer
  passes through as ordinary tokens: an unterminated string, quoted identifier or
  block comment (each would swallow the rest of the statement), and a character
  outside the dialect. A `--` comment cannot swallow the next line: it ends at the
  first line terminator (LF, CR, NEL, LS or PS), not only at LF, because a clause
  hidden inside the comment token is invisible to the leftover check (#1150). A
  rejected `ALTER TABLE` action yields a bare `Alter` expression, not a
  placeholder action. `SqlStatementCompletenessTests` enforces
  the rule: every profile clause and statement kind needs a corpus form, and every
  form followed by leftover text must report an error after the form.
- **Sealed AST nodes with internal constructors.** The parser is the only
  producer; consumers pattern-match. Extending the AST is a kernel change, which
  keeps downstream planners honest (no third-party node types appearing mid-plan).
- **Positions are offsets; line/column is presentation.** Nodes carry absolute
  character offsets. Mapping offsets to line/column belongs to the tool holding
  the source text (Roslyn's model) — carrying line numbers per node would bloat
  every node for a consumer that rarely needs them. Such a tool breaks lines by
  the rule `TokenLexer.CountLineBreaks` applies (every `IsLineTerminator`
  character, CR LF once), so its lines break where the lexer ends a `--`
  comment and agree with OQL and GQL diagnostic lines. The raw statement text
  is stamped once on the root (`SqlQueryExpression.Text`) after parsing.
- **Typed literal values are normalized by the parser (#1110).** SqlLiteralText is the
  public, strict invariant grammar in Sql.Language, alongside SqlTypeNames. It uses
  spans and BCL exact readers, with no Regex or reflection. Six literal kinds carry
  canonical text: dates and times, wall-clock timestamps, offset timestamps, day-time
  intervals and uppercase binary hex. N strings use String. The existing multi-word
  type grammar resolves temporal prefixes; X/N require token adjacency. Unsupported
  valid forms (including year-month intervals) report CK00001, invalid contents
  CS00008, both at the complete literal. Canonical rendering retains typed prefixes
  so persisted-expression equivalence checks kind and value without losing type.
  TIME and INTERVAL plain-string assignment share these readers with CAST (#1422).
  An unqualified bare signed interval number means seconds, following PostgreSQL's
  DecodeInterval default (src/backend/utils/adt/datetime.c:3625-3657). DATE and timestamp
  assignment retain SqlTemporalText's input acceptance; typed input reaches the strict readers directly.
  PostgreSQL gram.y:17806-17828 supplies the typed-string/interval split; datetime.c:3959-3977
  distinguishes date M from time M in ISO durations. X deliberately uses ISO binary semantics.
  PostgreSQL clock intervals accept explicit or implicit days and independently signed clock
  fields (datetime.c:3504-3596), with hh:mm, hh:mm:ss.fraction and mm:ss.fraction interpreted
  by DecodeTimeCommon (datetime.c:2656-2740). Hours are unbounded within TimeSpan, while
  seven fractional digits retain the dialect's 100 ns precision. SQL years-months
  (datetime.c:3669-3695) and ISO alternative dates (datetime.c:4038-4108) are recognized and
  refused as CK00001 rather than malformed CS00008. Day-time ISO P1DT2H remains executable.
- **String literal nodes carry the value, not the lexeme** — quotes stripped,
  doubled quotes unescaped — because every consumer (executor, planner, schema
  compiler) wants the value, and exactly one component (the parser) knows the
  escaping rules.
- **Delimited identifier nodes carry the identifier value.** Name consumption
  strips the surrounding double quotes for tables, schemas, aliases, columns,
  indexes and constraints. Tokens retain their quoted-identifier classification,
  so a quoted reserved word cannot become a keyword or literal. Keyword dispatch,
  literal parsing and diagnostics continue to consume raw token text. Doubled
  double quotes decode to one embedded quote; rendering escapes them the same way (#1235).
- **Type names resolve through one table.** `SqlTypeNames` is the single
  SQL-name → `DatabaseType` mapping (with the `DECIMAL(p[,s])`
  argument-is-precision rule); catalogs and the schema compiler must not grow
  their own copies. The mapping and modifier ranges live in the stateless internal shared source
  `SqlTypeModifiers`; `SqlTypeNames.TryResolve` refuses invalid modifiers and defaults an
  explicit decimal precision to scale zero. SQL, Sql.Schema and Sdk.Database compile the same
  source through `CohesionSharedSource`, with no new public seam or assembly-access grant.
  A name outside the table is not this package's error (phase E3):
  an engine's type catalog may register it as a domain, so a CAST target that is not
  a built-in name parses with no resolved type and no diagnostic when it has no
  arguments (domains take none; arguments produce `CS00005`). DDL retains normalized type text
  and a built-in's resolved `DatabaseTypeInfo` in `SqlColumnDefinition.ResolvedType`;
  the engine resolves remaining domain names and reports a name it does not know. Which conversions a
  CAST may make is the engine's cast registry's question too, so every built-in type
  is a valid CAST target here.
- **Recognized clause tokens stay in the lexer tables.** `RECURSIVE` is now
  advertised for binding; window functions are lexed so diagnostics can
  say "unsupported" precisely rather than mis-parsing them as identifiers. Gateable
  names live in `SqlClauses`; supported names are present in `SqlLanguageProfile`,
  while window and NATURAL clauses remain absent. Nested WITH and recursive
  execution retain positional capability checks. `ParseCore` scans a copy of its configured
  lexer before recursive descent and attaches the first rejected clause to the
  statement, so analyzers observe `CK00001` with the token location and the `SQL`
  surface name. An actually unknown leading command still receives `CS00002`, even
  when a later recognized token is also unsupported.

## ISO names and lexical aliases (#1108)

All three national names accept optional VARYING: NATIONAL CHAR VARYING, NATIONAL CHARACTER
VARYING and NCHAR VARYING resolve to unpadded String, like VARCHAR. Without VARYING they
resolve to padded Character, like CHAR; bare padded names default to length one, while
bare varying names are unbounded. This follows PostgreSQL src/backend/parser/gram.y:15130-15140.

CAST delegates its target grammar and validation to ParseDefinitionType, shared by column,
domain and function declarations. The grammar consumes only defined contextual continuations
(PRECISION, VARYING and time-zone suffixes), without enlarging the reserved list. The stateless
SqlTypeModifiers mapping supplies ISO names and D1 SQL Server lexical aliases to SQL, typed
schemas and SDK extraction. No reflection, Regex, new public type, interface or grant is needed.

MAX means unbounded only on VARCHAR, NVARCHAR and VARBINARY. NCHAR, NATIONAL CHAR and NATIONAL CHARACTER
retain #1437's padded Character identity. Every temporal precision, including 7, and INTERVAL
field qualifiers retain #1438's CK00001, because their semantics are not enforced. CS00004
stays retired for domains. BIT, SMALLDATETIME and TIME WITH TIME ZONE have CS00005 reasons
and replacements; arrays use CK00001. The planner consumes resolved column types directly.
The preflight scan skips complete temporal WITH TIME ZONE suffixes so they cannot become CTE
diagnostics. It walks the whole parenthesized modifier list, including malformed arguments and
nested parentheses, without validating it. Shared declaration/CAST parsing alone reports CS00005
for malformed lists; a well-formed temporal precision keeps CK00001. Canonical CAST rendering retains spaces between type-name words; parser/renderer
fixed points and persisted SQL function bodies cover these targets through reopen and the wire.

## Namespace note

Types live in `Assimalign.Cohesion.Database.Sql.Language`, matching the assembly
name (the repo rule). Earlier scaffolding used `...Database.Language.Sql`; the
rename happened before external consumers existed.

## Written-row projections (#1117)

The three DML nodes expose optional Returning lists. Parsing shares the SELECT list
grammar; the preflight resets VALUES/WHERE position flags on RETURNING so excluded
subqueries retain their precise name. OUTPUT, OLD/NEW and INTO are contextual IsWord
checks, preserving canonical identifier quoting and requiring no storage-format bump.
The profile advertises one new clause; DIALECT.md specifies its complete contract.
The OUTPUT alias stop applies only to INSERT and DELETE targets; UPDATE retains
its ordinary bare output alias before SET.

## Non-goals (current dialect)

CORRESPONDING, nested or data-modifying CTEs and recursive execution, window functions, LATERAL,
UPDATE SET subqueries, `ON UPDATE`,
RETURNING subqueries/OLD/NEW/INTO, savepoints, isolation-level syntax, and cost-hint syntax.
Each is an additive dialect extension when its engine feature
lands.

## Transactions and B7 extension points

`SqlTransactionExpression` identifies `BEGIN`, `COMMIT`, and `ROLLBACK` through
`SqlQueryCommandType`. `START TRANSACTION` and `BEGIN WORK` map to Begin, `END`
and `COMMIT WORK` map to Commit, and `ROLLBACK WORK` maps to Rollback (#1125).
`WORK` is the ISO synonym of the optional `TRANSACTION` noise word after
`BEGIN`, `COMMIT`, and `ROLLBACK`; START requires TRANSACTION and END takes no
suffix. These are spellings of existing clauses (+0): no `SqlClauses` constant or
profile entry is added. START and WORK match
contextually with ordinal, case-insensitive comparisons, keeping unquoted columns
named start/work bindable and keeping the schema renderer from quoting them.
END was already a keyword; its statement dispatch leaves CASE ... END unchanged.
The executor joins subsequent statements to the session's active scope, and
disconnect aborts that transaction. The language adds no concurrency machinery.
Malformed suffixes produce `CS00003` instead of silently treating `ROLLBACK TO`
as a complete rollback. END without a transaction retains the canonical
`CS04002` message, "COMMIT requires an open transaction."

The uniform `CS00009` language/syntax note (formerly the retired `CS00100`) applies to transaction statements too: omitting `;`
produces an informational diagnostic, with no error or warning for a valid spelling.
A valid spelling terminated with `;` produces no diagnostics. The transaction corpus
asserts both forms rather than exempting transaction control from the common policy.

The PostgreSQL grammar maps START TRANSACTION to its start node and END to its
commit node, with WORK/TRANSACTION sharing `opt_transaction`
(`src/backend/parser/gram.y:11358-11365`, `11471-11485`, local reference clone).
The family deliberately stops short of that broader grammar: END WORK and
END TRANSACTION, transaction modes, AND [NO] CHAIN, ROLLBACK [WORK] TO
[SAVEPOINT], BEGIN TRAN and named transactions, and COMMIT PREPARED remain
`CS00003`. ABORT [WORK|TRANSACTION], SAVEPOINT, RELEASE, SET TRANSACTION, and
PREPARE TRANSACTION remain `CS00002`. Recognition or recoding belongs to #1012.

B7 anticipates **`SAVEPOINT <name>`**, **`ROLLBACK TO [SAVEPOINT] <name>`**,
**`RELEASE [SAVEPOINT] <name>`**, and **`SET TRANSACTION ISOLATION LEVEL <level>`**.
Those forms remain outside this family, reserved for #1012. The session owns
exactly one root transaction through `DatabaseSession`, plus a defaulted value of
the existing transaction coordinator's `IsolationLevel`. Savepoints can add scope
markers within that transaction, and isolation syntax can set the carried value.

## Constraint normalization and persistence

Column and table declarations produce `SqlConstraintDefinition` nodes with the
same shape: optional name, ordered key columns, foreign-key target and deletion
action, or a check predicate. `SqlCreateTableExpression.Constraints` contains all
constraints; column definitions also retain their own constraints so `ALTER TABLE
ADD COLUMN` carries the same information. The executor consumes the normalized
table list once. The engine rejects duplicate key columns and multiple PRIMARY KEY declarations
with CS01019 before publication; it does not merge separate declarations (#1192). `CHECK` retains both its expression tree and the predicate source
as written (`CheckExpressionText`, for tooling); the engine never persists that
source. It persists the canonical text `SqlExpressionRenderer` renders from the
tree (below), so no spelling — and no leniency an older parser had for a spelling —
reaches storage. New constraint syntax follows the DDL parser's diagnostic recovery
discipline and emits `CS00003` on malformed input.

**`UNIQUE` is a unique index, not a new compiled-schema constraint kind.** The
syntax still has a `Unique` AST kind so it can preserve the declaration and name,
but execution lowers it to the same unique catalog index used by `CREATE UNIQUE
INDEX` and `CompiledSchemaIndex(IsUnique: true)`. This keeps compiled schemas,
SQL DDL, persistence, and concurrent uniqueness enforcement on one path.
Foreign keys and checks remain constraint catalog records. `ON DELETE` accepts
CASCADE, RESTRICT, NO ACTION, SET NULL and SET DEFAULT; an omitted action is NO ACTION.
`ON UPDATE` stays outside the profile and reports `CK00001`.

## Canonical rendering (`SqlExpressionRenderer`)

`SqlDefaultExpression` is a parser-owned, scopeless leaf for a whole INSERT VALUES
item or UPDATE SET value (#1193); canonical rendering emits `DEFAULT`. It never enters
the scalar precedence parser as an operand. INSERT/UPDATE share a contextual value
parser which recognizes the whole-item keyword before scalar parsing. The scalar parser
rejects bare DEFAULT everywhere with CS00003; qualified field names such as `t.default`
and `t."default"` use the normal column-reference parser. Quoted identifiers, comments
and strings retain their lexical identity; no token rescan is needed.
`SqlInsertExpression.IsDefaultValues` distinguishes one all-omitted row from a missing
source, an empty VALUES list or INSERT SELECT. A column list is allowed on that form
and remains available for target-name validation. No clause or profile entry is added.
The planner maps these forms to target-column assignment instructions; catalog default
binding and execution remain in Database.Sql, with no cross-assembly visibility grant.

`SqlExpressionRenderer.Render(SqlExpression)` and `Render(SqlQueryBodyExpression)` turn
a parsed tree back into SQL text. It is the one AST-to-SQL renderer: every
definition the engine persists — CHECK predicates and literal or expression DEFAULTs,
and future view queries (#1124) — is stored as its
output (the rule is in [DIALECT.md](DIALECT.md#persisted-definitions-are-canonical)).

- **The text is a function of the tree.** Keywords upper case, single spaces,
  `<>` for inequality, comments dropped, names and literal values as the tree holds
  them. Two spellings of one predicate store the same text, which is what lets the
  engine compare a compiled schema's CHECK with the catalog by canonical form.
- **Parentheses follow the parser ladder and preserve conservative persisted grouping.** The
  renderer assigns each node the rung `SqlQueryParser.Expressions.cs` parses it at
  and parenthesizes an operand when its position parses a tighter rung, with
  left associativity for logical and arithmetic operators and none for comparisons.
  IS operands retain comparison parentheses and LIKE pattern/ESCAPE operands retain
  concatenation parentheses (#1111), preserving existing canonical predicate text.
  Four spellings are special because the lexer or parser folds them: a sign
  applied to a sign is parenthesized (`--` would start a comment), `+(1)` keeps its
  parentheses (`+1` is the literal `1`), `NOT` before a plain `EXISTS` is
  parenthesized (preserving its unary node rather than the compact NOT EXISTS leaf), and a call's first argument whose
  text starts with `*` but is not the star itself is parenthesized (`f((* = 1))`,
  because `f(*` is read as the `COUNT(*)` star argument).
- **Identifiers are delimited only when needed**, mirroring the lexer's identifier
  scan: a keyword, builtin function name, unsupported-vocabulary or positional word,
  or a name the lexer would not read as one word is quoted. Embedded `"` characters
  are doubled inside those delimiters. A node type the parser does not produce is
  refused with `NotSupportedException`. A called function name is bare when it is a builtin
  or a plain word, except `CAST` and the nine window function names (`ROW_NUMBER`,
  `RANK`, ...): a bare call to one of those parses as the window function, which the
  dialect rejects, so an error-free tree that calls one named a delimited user
  function and keeps the quotes (`"rank"(x)`).
- **The invariant is tested, not assumed.** `SqlExpressionRendererTests` pins the
  canonical spelling of every form a CHECK accepts, proves that parsing the output
  yields the same tree (positions aside; a CAST target's whitespace is normalized)
  and that the output is a fixed point, and runs thousands of randomized
  expressions over the whole grammar through the same checks. WITH round trips
  compare the complete SqlTreeDump, including ordered definition names, column
  lists and bodies; generated query roots exercise WITH as well as scalar grammar.
  SEARCH and MATERIALIZED now render as quoted identifiers, including in persisted
  CHECK/DEFAULT/function text; the engine's format-16 gate prevents format-15
  canonical CHECK text from reporting provisioning drift under the new renderer.
  A parser change that
  would read stored canonical text differently fails that suite. The engine also
  re-parses each canonical text when it is first rendered and refuses to store a
  definition that does not reproduce its tree.
- **An `AND`/`OR` chain renders term by term** (#1151). Every term is rendered at
  the rung above the chain's, so a nested chain of the same operator keeps its
  parentheses. The parser merges a parenthesized chain only in first position, so
  no parsed chain holds one there, and the text is exactly what the nested binary
  form rendered: `a AND b AND c`, `a AND (b AND c)`, `(a OR b) AND c`. Definitions
  stored before chains became n-ary therefore read back to the same chain and keep
  their canonical text.
- **A tree deeper than any parser reads has no canonical text** (#1151): `Render`
  refuses a tree deeper than `SqlQueryParserOptions.MaximumExpressionNestingLimit`
  (4096) with `NotSupportedException` before walking it, because no parser would
  read the text back. That check is constant-time, and it keeps the walk's own
  recursion within the ceiling; the walk also checks the stack at every node, so a
  thread with little stack left gets `InsufficientExecutionStackException`, never an
  overflow. The text of a tree within the ceiling parses under every limit at least
  as deep as the tree, the limit of the parser that built it included.

## Expression nesting limit (#1151)

Recursive descent recurses once per level of the text, and every consumer walks
the tree it builds recursively too. Nothing bounded either, so `1 + 1 + ...` with
200,000 terms, or 200,000 nested parentheses, overflowed the stack. .NET cannot
catch a `StackOverflowException`: the parse, or the planner or evaluator after it,
ended the process, and on the server one statement ended every session. The first
pass bounded every tree at 128 levels, counting each link of an `AND`/`OR` chain.
The owner's follow-up decision (2026-10-01) is a middle course between SQL Server
and PostgreSQL: genuine nesting counts against one documented, configurable limit
(256 by default, 32..4096), a flat `AND`/`OR` chain counts once, and the stack
checks stay as PostgreSQL's backstop. The parser reports nesting past the limit as
`CS00006`; the user-facing rule, with the comparison table, is in
[DIALECT.md](DIALECT.md#expression-nesting-limit-1151).

- **`AND` and `OR` chains are n-ary, PostgreSQL's shape.** `SqlLogicalExpression`
  holds one operator and the chain's terms in source order; `ParseLogicalChain`
  builds one for each run of the operator at its rung, so a chain of any length is
  one level of the tree and a 10,000-term predicate is three levels deep. The parser
  no longer builds a `SqlBinaryExpression` with `And` or `Or`; the enum members stay
  for compatibility. A parenthesized chain of the same operator that opens a chain is
  merged into it, which is exactly what left associativity made of it before (both
  `(a AND b) AND c` and `a AND b AND c` were one left-leaning tree); one in a later
  position stays a nested node, as `a AND (b AND c)` stayed a right-nested tree. The
  n-ary tree is therefore the binary tree with each run of links collapsed, and the
  canonical text of every stored definition is unchanged. Flattening across later
  positions too, as PostgreSQL's planner does, would make `a AND (b AND c)` and its
  canonical text change shape, and would let the top-down depth count, which charges
  the nested chain's node while its terms parse, disagree with the final tree.
- **A chain costs its own terms, however it is grouped.** A chain that absorbs the
  parenthesized chain opening it takes over that chain's operand list
  (`SqlLogicalExpression.DetachOperands`) and appends to it, and it tracks its
  deepest operand as it adds each term, so building the node never rescans the list
  (the internal constructor takes the operand depth). The absorbed node is dropped.
  The first n-ary pass copied the list at every merge and recomputed the depth over
  it, so `((X AND t) AND t) ... AND t` cost the length of `X` once per pair of
  parentheses: a 1.45 MB statement under the 4096 ceiling parsed in 17 s and
  allocated 9.6 GB, against 0.5 s and 81 MB for the same chain written flat, and any
  wire client could send it. `SqlExpressionDepthTests` pins the parse of a
  20,000-term chain under 4,095 merges to the allocation of the flat chain.
- **Why only `AND` and `OR`.** They are associative under three-valued logic, and
  left-to-right short-circuit visits the terms of `(a AND b) AND c` and of
  `a AND b AND c` in the same order and stops at the same term, so one node changes
  neither the result nor which faults are raised (the #1069 contract). Arithmetic is
  not: `+` over BIGINT can overflow at a different point in another grouping, and
  `-`, `/` and `%` do not associate, so every other chain keeps a node per link and
  counts per link, as SQL Server and the first pass both do.
- **The limit is configured, not compiled in.** `SqlQueryParserOptions`, a
  `QueryParserOptions` subclass, carries `ExpressionNestingLimit` (default 256) and
  the range constants; the parser reads and validates it once, in its constructor,
  and keeps it in `_nestingLimit`. Any other options type gives the default, so
  existing callers keep a valid parser. The SQL engine passes its own configured
  value (`SqlDatabaseEngineOptions.ExpressionNestingLimit`).
- **Depth is a property of the tree.** Every `SqlExpression` carries an internal
  `Depth`, the node count of its longest path to a leaf, computed by its internal
  constructor from its children (and from `SqlSelectExpression.ExpressionDepth` for
  a subquery), so reading it is constant-time for a tree of any size. Measuring the
  tree, not the recursion, is what makes an arithmetic chain count: the parser
  builds `1 + 1 + ...` in a loop, but the result is as deep as it is long, and every
  later walker recurses through it.
- **Three checks make the bound exact.** Before the parser recurses into an operand
  that the node it is building will enclose (`ParseOperand`, and `ParseStarArgument`
  for the `*` of `COUNT(*)`), it counts that node in `_expressionDepth` and rejects
  the statement if the node and a leaf under it would already exceed the limit.
  Whenever it builds a node over an operand parsed before the node existed (each
  link of an arithmetic chain, a comparison, `IS NULL`, `BETWEEN`, `IN`, `LIKE`,
  `COLLATE`, an `AND`/`OR` chain), `Nest` adds the node's `Depth` to the enclosing
  count. An `AND`/`OR` chain is also checked at its first operator (`TryOpenChain`),
  against its first operand or the terms of the group it absorbs, so a chain that is
  already too deep is rejected before its remaining terms are parsed. Every other
  node is bounded by the checks its operands passed on the way down, so a statement
  is rejected exactly when its tree is deeper than the limit, counting subqueries,
  whose clauses parse one level below the subquery node. The first pass added the
  star of `COUNT(*)` without a check, so a call at the deepest level built a tree one
  level deeper than the limit.
- **The statement records how deep it nests.** Each check that passes also records
  the depth it saw (`Deepen`: every primary at its level, every `Nest`ed node, every
  parenthesis), and `ParseCore` stamps the greatest on the root, which
  `SqlQueryStatement.ExpressionNestingDepth` exposes. Because the checks are exact,
  a parser with any limit at least that large accepts the statement and one with a
  smaller limit rejects it, so an engine with a lower limit than the parser that
  built a typed request refuses it on that number alone, without parsing the text
  again. The parser stamps only the root, so a query it did not return as a root, a
  subquery a caller takes out of a parsed statement and wraps in a statement of its
  own, falls back to the depth of its own tree (`SqlQueryExpression.NestingDepth` is
  the greater of the stamp and `SqlSelectExpression.ExpressionDepth`; `SELECT` is the
  only query kind a statement nests). Both are cached, so the read stays
  constant-time, and the fallback is what the engine's walks recurse through: such a
  query is never parsed again, so its parentheses no longer matter.
- **Parentheses are bounded apart from the tree.** They are not nodes, so they do
  not change `Depth`, but each pair is a level of recursion, so `_parenthesisDepth`
  caps them at the limit too. Folding them into the tree count would break persisted
  definitions: the renderer parenthesizes a sign applied to a sign (`- -1` is stored
  as `-(-1)`), so stored text can carry more parentheses than the declaration did.
  It adds at most one pair per node, so the stored text nests its parentheses no
  deeper than its tree, which is the declared tree, and it always parses back.
- **Recursion is bounded by construction, not by the limit's size.** Every cycle
  through the expression rules passes `ParseOperand` (or `ParseSubquery`, which
  enters the subquery node the same way) or a parenthesis, so the parser recurses
  at most a limit's worth of node levels plus a limit's worth of parenthesis levels.
  Each entry also calls `RuntimeHelpers.TryEnsureSufficientExecutionStack`. A
  parenthesis or call level runs the whole precedence ladder, measured at about
  0.7 to 1.9 KB of stack in a release build, depending on how far the JIT has
  optimized the parser, and about 5 to 6.5 KB in a debug build. In a release build
  the deepest text the default limit accepts, 256 parentheses around 255 nested
  calls, parses on a 1 MB thread; in a debug build it needs about 3 MB, so the tests
  parse at the limit on an 8 MB thread. A higher configured limit admits text no
  default thread can recurse through. A thread with too little left reports
  `CS00007` rather than overflowing. It is a separate code from the limit's `CS00006`
  because the text is within the dialect and parses on a bigger stack; the engine
  relies on the difference to tell a thread too small for a stored definition apart
  from a damaged catalog, and reports it to clients as `CS05004`, statement too
  complex, like any other walk out of stack. Collapsing the binary rungs into one
  precedence-climbing loop would cut the cost per level, should smaller threads ever
  need to accept the deepest text the default limit admits in a debug build.
- **Scope checking belongs to binding.** The parser admits qualified and unqualified
  outer references without a post-parse correlation walk. The engine searches lexical
  scopes using catalog metadata; deep expression chains still parse within their
  configured budget on a small stack.
- **Crossing the limit abandons the statement.** The parser reports `CS00006` (or
  `CS00007`) once, consumes the rest of the text, and every rule still on the stack
  meets the end and returns. What those rules report on the way out describes the
  abandoned text, not the statement, so `ParseCore` drops every diagnostic after it and keeps
  only the statement's command type, as it does for a character outside the dialect.
  The scans that run over the whole text before parsing (lexical errors and the
  unsupported-clause preflight) are unaffected and keep their diagnostics.
  Recovering past the deep operand instead would mean parsing it, or skipping it by
  matching parentheses and `CASE ... END` without parsing; abandoning keeps the
  error single and the partial tree out of every consumer's hands.

## Unary plus

ISO unary plus is `SqlUnaryOperator.Plus`, parsed on the unary rung beside `-`.
Directly before a numeric literal the sign stays part of the literal, so `+1` and
`1` are the same tree and `ORDER BY +1` keeps its ordinal meaning; `+a`, `+(1 + 2)`
and `+@p` build a unary-plus node. Typing and evaluation belong to the engine.

Column DEFAULT uses the restricted comparison/distinct grammar, including arithmetic,
unary signs, CAST and scalar calls (#1121, #1111). Negative numeric literals retain the same unary-minus node as a scalar
expression. Compiled-schema defaults remain literal-only; DEFAULT assignment (#1193) reuses
the canonical expression and resolved calls the catalog stores.

## AOT posture

Hand-written parser over the zero-allocation `TokenLexer` ref struct; no
reflection, no grammar codegen.

## E4 grammar

SqlQueryParser.Definitions parses single-expression CREATE FUNCTION, DROP FUNCTION with its
parameter-type signature, and CREATE/DROP DOMAIN. Immutable AST nodes carry bodies, domain
checks/defaults and declaration attributes. Syntax belongs here; namespace, volatility, recursion,
ownership and binding belong to the engine. DIALECT.md defines the supported clauses and errors.
Function parameter and return type text retains identifier delimiters until the engine resolves
it, so a quoted type name's punctuation cannot be mistaken for type arguments.

## Declaration modifiers (#1438)

CAST and ParseDefinitionType apply SqlTypeModifiers before producing an executable statement.
The latter serves columns (CREATE, ADD and ALTER), domain bases, and function parameters and
results, with an object-specific diagnostic subject. CS00005 now covers malformed and invalid
modifiers on every type spelling, including a domain given arguments. CS00003 remains for
surrounding syntax errors. SqlTypeNames.TryResolve returns false for an unknown name or invalid
modifiers; name-only resolution still distinguishes known built-ins. The shared splitter preserves
quoted identifiers and reads at most two unsigned Int32 arguments; typed schemas admit
identifiers and the closed built-in multi-word names, and recognize interval qualifiers for the unsupported-modifier
diagnostic. DIALECT.md records the exact ranges and unbounded defaults.

`NormalizeScale` is the shared decimal default: an explicit precision with no scale means zero;
a bare decimal stays unconstrained. Compiled-schema snapshots, their validation, the typed
builders, SDK extraction and engine provisioning use this same rule as SQL and CAST resolution.

FLOAT(p) admission and resolution follow PostgreSQL's `gram.y:14991-15016`: 1..24 bits selects
Float32, 25..53 selects Float64, and bare FLOAT is Float64. The resolved type carries no length,
decimal precision or scale. Out-of-range FLOAT precision remains CS00005. Temporal precision
(TIME, TIMESTAMP/DATETIME, TIMESTAMPTZ and INTERVAL) is valid PostgreSQL syntax whose rounding
is not implemented here: the shared rule returns CK00001 with the modifier, and parser callers
attach the declaration or CAST target. The rule distinguishes these unsupported modifiers from
malformed argument lists, which remain CS00005. A single unsigned precision above six is also
recognized, because PostgreSQL clamps it with a warning; DIALECT.md records the difference.

The declaration parser and CAST share interval-field consumption before argument parsing.
SqlTypeModifiers owns the six fields and seven accepted X TO Y ranges, following PostgreSQL
`src/backend/parser/gram.y:15197-15267`. Only SECOND (alone or at a range's end) takes precision.
Every valid qualifier reports one CK00001 naming the declaration and complete modifier, including
forms without precision. Invalid ranges remain CS00003; malformed arguments remain CS00005 and
suppress a secondary unsupported diagnostic. Case and token trivia do not change admission.
Delimited names retain their lexical identity so a domain named `"INTERVAL DAY"` is still a domain.

## Quoted function recognition (#1103 review)

The function-call branch checks quoted names against the same recognized unsupported table
as unquoted calls. Quoted names must match the lowercase lookup spelling exactly; unquoted
names match without regard to case. Parentheses distinguish calls from quoted columns.
DIALECT documents the PostgreSQL scanner and function-lookup references and examples.

Rendering preserves accepted quoted uppercase and mixed-case window-like names with delimiters.
A lowercase quoted unsupported call retains its diagnostic and NULL recovery expression; the
renderer round-trip guarantee continues to apply to error-free parser trees. Renderer tests
cover both accepted spellings and the coded recovery of rejected spellings.

Definition-binding diagnostics remain engine-owned. DIALECT.md records CS02003 for non-Boolean
CHECK predicates and CS03301 for invalid definitions and absent DROP definitions, including
the structured carrier and span fallback.

Wire diagnostic overflow does not change parser admission or its complete in-process
carrier. The endpoint bounds delivery with an explicit CK07001 prefix/omission notice
while retaining the original failure and session; DIALECT.md records this wire behavior.

## Persisted view statements (#1124)

SqlCreateViewExpression and SqlDropViewExpression are typed CREATE/DROP nodes;
view bodies use the existing SqlQueryBodyExpression grammar, including WITH and
set operations. VIEW remains a keyword; REPLACE is accepted as its function token.
View modifiers remain contextual so persisted identifier quoting does not change.
Capability scanning treats the body after AS as a query, preserving the same
unsupported-SELECT diagnostic at declaration time. DROP VIEW RESTRICT and CASCADE
are parsed independently of the foreign-key action capability flags.
The engine's view metadata contract is documented in DIALECT: complete common type modifiers
flow through scalar alternatives and set operands, every catalog view column is nullable, and
catalog refusals retain structured diagnostics over the wire.

## Distinct predicates, truth tests and LIKE ESCAPE (#1111)

Dedicated SqlDistinctExpression and SqlBooleanTestExpression nodes retain negation;
SqlTruthValue selects TRUE, FALSE or UNKNOWN. SqlLikeExpression adds an optional Escape
operand. Depth accounting, canonical rendering and the complete tree dump include every
operand. The parser uses PostgreSQL's IS/comparison/predicate/ESCAPE ladder and separates
concatenation from addition; no named clause or keyword is added. ILIKE/SIMILAR are refused
positionally, so ordinary identifier quoting stays unchanged. ISNULL/NOTNULL are likewise
postfix capability refusals. Both list and subquery IN/NOT IN forms complete at the
closing parenthesis and can feed another predicate (gram.y:15632-15668); binary
comparison, distinct, LIKE and BETWEEN rungs still refuse chaining once at the second operator. Unary signs
bind above COLLATE (gram.y:910), and NOT EXISTS resumes the ordinary NOT operand
grammar before choosing the compact leaf. DIALECT.md specifies behavior.

Concurrent FK catalog refresh is engine-owned. DIALECT records retryable CS04006
for an invalidated target plan; adjacent definition changes rebuild before row reads.

## Temporal text conformance (#1422)

The shared TIME reader accepts single-digit hours, AM/PM, a trailing fractional dot and a
validated discarded zone; timestamp's clock reader remains zone-free. INTERVAL accepts implicit
days before hour units, @ and final ago. Both readers reject non-ASCII whitespace. Generated
ErrorMessages resources hold literal-reader failures. DIALECT records the bounded zone vocabulary,
TimeOnly/fraction limits, preserved CAST diagnostics and format-26/v8 fence.

## Execution diagnostic corrections (#1653)

Temporal readers live in the existing shared-source unit and report an explicit format, field-range
or interval-overflow reason. The public literal facade preserves FormatException for grammar errors
and now reports OverflowException for out-of-range calendar/clock/offset fields. SQL assignment, DEFAULT
and CAST translate the same reason into 22007, 22008 or 22015 without adding a public bridge or assembly
reference. Unknown COLLATE identifiers now reach binding (42704); unsupported collation creation
retains CK00001. DIALECT records the corrected mappings and persisted-expression span provenance.

## SELECT-only EXPLAIN ANALYZE (#1522)

SqlExplainExpression.Analyze distinguishes execution analysis from a plain nonexecuting description.
ANALYZE is contextual after EXPLAIN; standalone statistics maintenance is not introduced. The engine
accepts SELECT query bodies only for execution analysis. DIALECT.md defines candidate counts,
bounded probe estimates, budget refusals and the unchanged result shape.

## Sql diagnostic conventions (#1470)

Exception and diagnostic templates use the project's build-generated `ErrorMessages` resources.
Parameterized templates format with the current culture; abort/cancellation templates used in
normal cleanup are cached. The [Database error-code registry](../../Assimalign.Cohesion.Database/docs/ERROR-CODES.md)
assigns this family its two-letter prefix and categorized five-digit identities.
Existing SQLSTATE meanings, numeric wire error kinds, frame layouts and disk formats are preserved.
Structured diagnostics keep their code outside the message. Compiler diagnostic identifiers and
standard .NET argument guards retain their own contracts.


The shared Types module also binds modifier-refusal templates to each consumer's own generated
resources through internal `SqlTypeModifierErrorMessages`. Unsupported type/modifier text and
codes are preserved. Numeric modifier spelling remains invariant SQL text; message templates
format with the current culture. Successful type validation adds no resource lookup.

The validation reference was rechecked in the local PostgreSQL clone: src/backend/utils/adt/varchar.c:33-57 refuses invalid modifier counts and character lengths; src/backend/parser/gram.y:14991-15016 bounds FLOAT precision. This convention change preserves Cohesion's existing validation behavior and only moves its text; no reference-engine code was copied.

## Rules audit after main cleanup

Parser nesting refusal templates use the generated ErrorMessages accessor; their existing
codes, text, limits and locations are preserved. The assembly-local Types resource binding now
lives under Internal. Private constants use repository casing and extension containers are
partial. Successful parsing adds no resource lookup or grammar change.

## Error message producers and catalog carriers

Human diagnostic context (implicit-alias recovery, token descriptions, expected delimiters and
type-declaration subjects) uses generated resources without changing any code, span or text.
The parser caches these templates in static readonly fields because expected-token descriptions
are also evaluated on successful parses; it adds no instance fields or repeated resource lookup
on that path. Canonical SQL, recognized construct names and token spellings remain data.

The dialect records CS06059/XX001 for malformed persisted SQL catalog metadata and CS06060/0A000
for unsupported metadata formats. These engine-side carrier corrections do not change grammar,
accepted storage formats or the no-upgrade policy.

## Debugger inspection

Concrete source types with explicit nonpublic instance state use a private nested DebugView
and debugger display/proxy attributes. The view is the last member in the same type file and
exposes only reviewed public/protected state through read-only properties. Exception and
EventSource types remain excluded. A type with no state to filter keeps its normal member view;
a type whose base supplies both debugger attributes inherits that view.

Inspection does not perform I/O, take lifecycle locks, mutate state, invoke application callbacks,
or materialize lazy results. The additions are debugger metadata and nested helper types; existing
instance fields, runtime operations, error identities, data formats and protocol contracts do
not change. SDK and Studio source are outside this rule's scope.

The concrete SqlQueryExpression base supplies the inherited view for its derived query
expressions. Its projection uses nonvirtual CommandType; Text is omitted because an application
subclass can override that getter. Scalar-expression views expose existing syntax data without
walking depth or diagnostics enumerations. Parser inspection does not advance lexers or initialize
a language profile through its protected getter.
