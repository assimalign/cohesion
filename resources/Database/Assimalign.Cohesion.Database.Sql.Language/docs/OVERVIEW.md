# Assimalign.Cohesion.Database.Sql.Language — Overview

The SQL language front-end of the Cohesion Data Platform: a recursive-descent
parser (`SqlQueryParser`) producing a full clause-level AST over the shared lexer
infrastructure (`Database.Language`), the declared dialect contract
([DIALECT.md](DIALECT.md)), and the type-name translation table (`SqlTypeNames`)
binding SQL DDL/CAST type names to the shared type system (`Database.Types`).

## Scope

- **Parser** — UNION/INTERSECT/EXCEPT with ALL or DISTINCT in every query position, non-recursive `WITH` before SELECT/INSERT/UPDATE/DELETE, plus `SELECT` (INNER/LEFT/RIGHT/FULL/CROSS joins, parenthesized join groups, derived tables, grouping, ordering, limits, subqueries), `INSERT`
  (multi-row, whole-item `DEFAULT`, `DEFAULT VALUES`, `INSERT ... SELECT`), `UPDATE`
  (including parsed/bound FROM sources, `SET c = DEFAULT` and correlated and uncorrelated WHERE subqueries), `DELETE`
  (including parsed/bound USING sources and correlated and uncorrelated WHERE subqueries), `CREATE/ALTER/DROP TABLE`, `TRUNCATE` table lists with identity and FK policies,
  `CREATE TABLE AS` with complete output names and WITH [NO] DATA, `CREATE/DROP FUNCTION`, `CREATE/DROP DOMAIN`, and a full expression grammar (precedence, `CASE`, `CAST`, predicates,
  parameters). Error-tolerant: malformed input yields diagnostics, never
  exceptions.
- **AST** — sealed statement/expression node families under
  `SqlQueryStatement`/`SqlQueryExpression`; the raw statement text is stamped on
  the root after parsing. An `AND` or `OR` chain is one n-ary
  `SqlLogicalExpression` of any length. SELECT and set-operation query bodies share
  `SqlQueryBodyExpression`, including WITH definitions and combined result tails.
- **Nesting limit** — `SqlQueryParserOptions.ExpressionNestingLimit` (256 by
  default, 32..4096) bounds how deep an expression and its parentheses nest
  (`CS00006`); a flat `AND`/`OR` chain counts one level. The statement records
  how deep it nests (`SqlQueryStatement.ExpressionNestingDepth`). See
  [DIALECT.md](DIALECT.md#expression-nesting-limit-1151).
- **Dialect contract** — [DIALECT.md](DIALECT.md) is the authoritative supported /
  recognized / rejected matrix. `SqlLanguageProfile.Instance` carries its lexical
  tables and implemented clause set; recognized clauses outside that set report
  `CK00001` rather than an undifferentiated parse failure.
- **Literal text** — public `SqlLiteralText` reads strict invariant dates, times, timestamps,
  day-time intervals and hexadecimal bytes. Typed literals normalize before binding;
  `N'...'` is String.
- **Types and builtins** — `SqlTypeNames` resolves declared type names (with
  length/precision/scale) to `DatabaseType` identities; builtin function names are
  declared in the lexer tables and the dialect doc.
- **Canonical rendering** — `SqlExpressionRenderer` turns a parsed expression or
  SELECT, set-operation, UPDATE or DELETE query back into canonical SQL text that parses to the same tree. It is the
  form in which the engine persists stored expressions (CHECK, DEFAULT and SQL function bodies); see
  [DIALECT.md](DIALECT.md#persisted-definitions-are-canonical).

UPDATE FROM and DELETE USING execute and belong to the executable clause profile. EXPLAIN
exposes target access paths and source join algorithms. UPDATE refuses multiple matches before
writes with CS05068; DELETE deduplicates physical targets.

## Dependencies

`Database.Language` (lexer, parser base, diagnostics) and `Database.Types`
(type identities). Consumed by `Database.Sql` (the engine) and, later, the SQL
catalog/planner and SDK schema compiler.

## Usage

```csharp
var parser = new SqlQueryParser();
var statement = (SqlQueryStatement)parser.Parse("SELECT id FROM users WHERE age >= 21;");

var select = (SqlSelectExpression)statement.SqlExpression;

// Canonical text: "age >= 21"
string canonical = SqlExpressionRenderer.Render(select.Where!);

// A stricter nesting limit than the default 256 levels.
var strict = new SqlQueryParser(new SqlQueryParserOptions { ExpressionNestingLimit = 64 });
```

See [DESIGN.md](DESIGN.md) for the parser's shape and the decisions behind it.

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
