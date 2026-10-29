# Assimalign.Cohesion.Database.Sql.Schema — Overview

CompiledSchemaConstraint exposes all five OnUpdate actions, defaulting to NoAction. The v9
document persists the action and migration equality includes it. Earlier artifacts are refused;
rebuild artifacts and recreate affected databases (#1723, SQ-F1).

Code-first objects are provisioned into the SQL namespace `dbo`. SQL `CREATE SCHEMA` creates
other namespaces for SQL-authored objects; builders have no namespace-targeting option. A
compiled schema's name identifies ownership independently of a table's SQL namespace (#1030).

SQL's thin schema package provides one-step `SqlSchema.Compile`, the lower-level
`SqlSchema.Create` with the opaque declaration it returns and its own `Compile()`, the
sealed declaration builders, the standalone `SqlCompiledSchema` (format
`cohesion/database-schema/v9`, its canonical document and SHA-256 computed once), its canonical
serializer, SQL migration planning and `SqlSchemaMigrationResult`. Both SDK build tooling and the
SQL engine consume it; the Database area root holds no schema type.
A retained `SqlSchema` keeps its compiled result, so reusing it in subsequent engine builds also
reuses the computed document and hash.

Compiled tables, indexes, and constraints are schema-owned. The SQL catalog
persists that ownership and the engine protects those objects from session DDL.

A table declares a CHECK with `table.Check(name, sql)`: the SQL text is kept as written in
the document and hash, and the SQL engine's build binds it to its registered functions
before it touches a file. Pass constant strings, so the SDK can extract it.

A column of a named SQL type is `table.Column(selector, "email")` (a domain the SQL engine
registers, or a built-in name such as `"VARCHAR(320)"`), and `schema.Type<T>(t => t.Decimal(p, s))`
declares a domain named for `T` that every member of `T` uses. The engine resolves the names when
it builds; the migration planner plans `AddDomain` and `DropDomain`.

`table.Default(row => row.Status, "pending")` declares a string value and renders `'pending'`;
`Default(row => row.Quantity, 7)` renders `7`. The typed API also accepts Boolean, nullable,
numeric, char, enum, date/time, Guid and binary values, using invariant SQL literals.
TimeSpan defaults use signed total seconds with a `seconds` unit, retaining exact ticks.
`table.DefaultSql(selector, "'pending'")` supplies raw SQL literal text as an escape hatch.
The compiled column keeps the rendered text in `DefaultLiteral`, so equivalent typed and raw
declarations have identical documents and hashes in the [current schema format](DESIGN.md#fixed-length-schema-columns-1437). The SDK uses the same
renderer for C# compile-time constants and directs nonconstant declarations to `DefaultSql`.
The SQL engine validates defaults before provisioning, and they survive canonical documents
and catalog reconstruction after an interrupted migration.

See [DESIGN.md](DESIGN.md) for boundaries, canonicalization, and AOT decisions.

## Sql diagnostic conventions (#1470)

Exception and diagnostic templates use the project's build-generated `ErrorMessages` resources.
Parameterized templates format with the current culture; abort/cancellation templates used in
normal cleanup are cached. The [Database error-code registry](../../Assimalign.Cohesion.Database/docs/ERROR-CODES.md)
assigns this family its two-letter prefix and categorized five-digit identities.
Existing SQLSTATE meanings, numeric wire error kinds, frame layouts and disk formats are preserved.
Structured diagnostics keep their code outside the message. Compiler diagnostic identifiers and
standard .NET argument guards retain their own contracts.


## Shared defaults and diagnostic resources (#1470)

The Defaults shared module resolves incompatible-value text through a consuming-assembly-local
`SqlDefaultLiteralErrorMessages` binding to the generated internal resource accessor. SQL schema
and SDK extraction therefore retain identical ArgumentException text and the `value` parameter,
without a resource lookup on successful rendering. SQL explanatory message fragments also use
resources. SDK compiler identifiers remain `COHDBSDK`; embedded runtime identities use `CK00001`
for language syntax and `CS03101` for unsupported principal declarations. Schema format remains
`cohesion/database-schema/v9`; SQLSTATE, wire, diagnostics carriers and public APIs are unchanged.


The shared Types module also binds modifier-refusal templates to each consumer's own generated
resources through internal `SqlTypeModifierErrorMessages`. Unsupported type/modifier text and
codes are preserved. Numeric modifier spelling remains invariant SQL text; message templates
format with the current culture. Successful type validation adds no resource lookup.

## Rules audit after main cleanup

Assembly-local Defaults/Types resource bindings live under Internal while preserving their
linked owning namespaces. SqlSchemaMigrationResult lives in ValueObjects with the same public
namespace, positional properties and construction. Canonical documents, hashes and format
cohesion/database-schema/v9 are unchanged.

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

Compiled-schema views expose already constructed declaration metadata. CanonicalDocument
and Hash are omitted: those getters serialize/hash lazily and update computation counters.
Inspection does not call Compile, evaluate a schema callback, or expose private builder records.
