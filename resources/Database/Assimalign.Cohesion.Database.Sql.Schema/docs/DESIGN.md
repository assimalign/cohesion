# Assimalign.Cohesion.Database.Sql.Schema — Design

## SQL type aliases (#1108)

All three national names accept optional VARYING: NATIONAL CHAR VARYING, NATIONAL CHARACTER
VARYING and NCHAR VARYING resolve to unpadded String, like VARCHAR. Without VARYING they
resolve to padded Character, like CHAR; bare padded names default to length one, while
bare varying names are unbounded. This follows PostgreSQL src/backend/parser/gram.y:15130-15140.

Shared SqlTypeModifiers admits the closed ISO multi-word name set and SQL Server lexical
aliases in typed column declarations and SDK extraction. VARCHAR(MAX), NVARCHAR(MAX) and
VARBINARY(MAX) are unbounded; NCHAR, NATIONAL CHAR and NATIONAL CHARACTER normalize to padded Character
like CHAR. Every temporal precision remains unsupported (COHDBL001 detail). The canonical
format advanced v6 -> v7: an earlier NCHAR CustomType could mean a domain stored as String,
whereas it now normalizes to Character. Earlier artifacts are refused without normalization
or an upgrade reader. Aliases retain CustomType; Character normalization persists type and
length. Migration DDL uses these names. SQL format 13 independently refuses earlier databases.

## Fixed-length schema columns (#1437)

The current canonical document is **cohesion/database-schema/v8**, shared with Sdk.Database.
CLR char maps to Character with length one; a string member declared with CHAR(n)/CHARACTER(n)
normalizes to Character and n (bare CHAR means one). Explicit lengths use the existing shared
SqlTypeModifiers rule. A CHAR/VARCHAR change is an AlterColumn type change, requiring the existing
destructive-change opt-in; it is never an equal-hash/no-op. Earlier documents, including v5,
are refused without normalization or compatibility readers. Runtime and SDK render Character as
CHAR(n) and retain the author-provided CustomType name when present.

This package owns SQL schema declarations, immutable compiled relational shapes,
canonical JSON, validation, and migration planning. Feature A3 of
[DATABASE_MVP_FEATURES.md](../../../../docs/programs/DATABASE_MVP_FEATURES.md)
moves that vocabulary out of the model-agnostic Database area root.

The package directly references only `Assimalign.Cohesion.Database` and
`Assimalign.Cohesion.Database.Types`. The SDK's build task can compile a schema
through this seam without taking a dependency on the SQL engine, SQL storage,
Connections.Tcp, or Hosting. The area's root independently composes its generic
child roots; this package does not invert that direction.

`SqlSchema.Compile(name, configure)` is the ordinary composition-root entry point: it
declares and compiles a SQL schema in one step. It is `SqlSchema.Create(name, configure)`
followed by the declaration's own `Compile()`, so validation, canonical documents, and
hashes are identical on both paths. `Create` returns an opaque `SqlSchema`, built by the
sealed `SqlSchemaBuilder`, `SqlTableBuilder<TRow>`, `SqlTypeBuilder` and
`SqlPrincipalBuilder`, for build tooling and callers that compile a declaration they did
not author. The internal `SqlSchemaCompiler` lowers that retained C# declaration into
`SqlCompiledSchema`, which carries its format, the database name, the destructive-change
opt-in, and the SQL tables, columns, keys, indexes, constraints, types, principals and
grants.

Since B2, a retained `SqlSchema` also caches its compiled result. Every builder snapshots its
collections when its callback ends, including a principal's caller-supplied grant names; later
builder calls cannot change the declaration. Reusing that declaration in a later engine Build
therefore reuses the immutable compiled shape, its canonical document and its hash. Concurrent
first compiles may duplicate the same deterministic work, as concurrent first identity reads may.

**Standalone since B1** (owner decisions 50, 57 and 59 of 2026-10-09,
`docs/programs/DATABASE_ENGINE_EXTENSIBILITY_DESIGN.md`). `SqlCompiledSchema` used to derive the
area root's model-agnostic `CompiledSchema`, which carried an engine `Model` and recomputed the
canonical document and its SHA-256 on every read (one apply read each twice). The root keeps no
schema type now; the compiled schema owns its `Format`, `Name` and `AllowsDestructiveChanges`, and
computes `CanonicalDocument` and `Hash` once, on first read (the schema is immutable, so a race on
the first read costs only duplicate work). Dropping `Model` changed the document, so the format moved to
`cohesion/database-schema/v2` at the time (a `v1` document no longer deserialized, and a database
whose recorded state was `v1` was replanned from its catalog snapshot). The format is now
`v8`: #1422 changes temporal default text; #1108 adds NCHAR normalization after #1437 adds Character identity after #1438 normalized an omitted decimal scale, which changes the canonical document, so every
earlier document — an SDK artifact, an applied marker or a pending migration target — is refused
by the format check rather than replanned. The root's `SchemaMigrationResult` moved here as `SqlSchemaMigrationResult`.
The C# lambda `Function<…>` and `Trigger<…>` declarations, the `Extension` value, their compiled
records (`CompiledSchemaFunction`, `CompiledSchemaTrigger`, `CompiledSchemaExtension`,
`CompiledSchemaParameter`), `SqlTriggerContext`, `SqlTriggerEvent` and the
`SqlSchemaValidationErrorCode.ModelMismatch` value were deleted with it: their canonical C# text was
never executed, and the SQL engine refused any schema that declared one. A SQL engine's functions
are registered on its engine builder instead (E2). A reference whose column type does not match its
target's key, which the compiler reported as `ModelMismatch`, is `UnsupportedType`, as the
validator already reported it.

Tables, indexes, and constraints in a compiled schema always report
`DatabaseObjectOwner.Schema`: applying them is code-first provisioning, and
session DDL must not mutate the resulting catalog objects. The SQL engine
persists ownership and enforces the lock; this package requires no engine
implementation to express that contract. Ad-hoc catalog objects remain fully
mutable through session statements.

The removed `CompiledSchemaCollection` and collection planning do not belong to
SQL. The document model will define its own shape in its own model family.
There is no collection member or compatibility placeholder on the SQL schema.

Canonical JSON uses source-generated System.Text.Json metadata. Constructors
snapshot inputs and sort semantic sets; column order remains significant.
`CanonicalDocument` and `Hash` are excluded from their own serialization.
Validation rejects malformed input, unknown JSON members, an unsupported format,
duplicate declarations, missing references, and unsupported value types.
The owning test assembly reads internal computation counters to prove that repeated identity
reads do not serialize or hash again. These hooks add no public or protected surface.

`SqlSchemaMigrationPlanner` produces deterministic dependency-ordered operations
and requires the declaration's destructive-change opt-in for data-losing steps.
Unsupported metadata changes (principals) fail explicitly. SQL identifiers compare
without case. Applying a plan is the SQL engine's (`Database.Sql`), which reports the outcome as a
`SqlSchemaMigrationResult` (`FromHash`, `ToHash`, `OperationCount`, `WasAlreadyApplied`).

**Domains and column SQL types (E3, owner decision 68; format `v3`).** A declared type,
`schema.Type<T>(t => t.Decimal(p, s))`, lowers to a domain of the SQL engine: the compiled
`CompiledSchemaType.Name` is `T`'s simple name, which must be a SQL identifier (a generic, nested
generic or array type is `UnsupportedType`, naming the CLR identity), and every column whose
member is of `T` is declared with it (`CompiledSchemaColumn.CustomType`, with the precision and
scale). `table.Column(selector, sqlType)` declares a column of a named SQL type, a domain the
engine registers (`"email"`) or a built-in type name with its arguments (`"VARCHAR(320)"`): the
column keeps its member's storage type and the name as written, without length, precision or
scale, and a member declared with two names is refused at the second. This package does not know
the engine's domain registrations; it checks the built-in modifier ranges as well as the name's shape (an identifier or closed built-in multi-word name, optionally integer arguments or supported MAX; `CompiledSchemaTypeNames`), which also keeps the name safe to write
into DDL; the SQL engine's build resolves it, built-in names first, and refuses an unknown name or
a type not stored as the member is, before it touches any file. Because a declared type's name and
a column's type changed meaning, E3 moved the document format to `cohesion/database-schema/v3`; a `v2`
document no longer deserializes; recorded documents of earlier formats are refused at open. The
validator accepts a column `CustomType` the schema does not declare when it has the shape (the
engine's domain or a built-in name), and checks the storage type of one it declares. The planner
plans `AddDomain` before the tables (so columns can be declared with the domain) and `DropDomain`
after them, both safe and carrying the `CompiledSchemaType` (`SqlSchemaMigrationOperation.Domain`);
a redefined domain is a drop and an add, and the columns declared with it change through their own
`AlterColumn` steps. A domain is registered on the engine, not stored in the database, so a SQL
engine renders no statement for either step.

**Column defaults (B2 round 2, owner decision 2026-10-11).**
`table.Default<TValue>(selector, value)` declares a column's default from a CLR value, including
the column if no earlier verb mentioned it. `Default(row => row.Status, "pending")` renders
`'pending'`, `Default(row => row.Quantity, 7)` renders `7`, and Boolean and null values render
`TRUE` / `FALSE` and `NULL`. The generic method belongs to the sealed table builder, as
`database-area.md` rule 5 permits. Nullable members infer their nullable value type without
another overload. A string passed to `Default` is always a value, never SQL text.

The stateless internal `SqlDefaultLiteralRenderer` in this package's `shared/` folder owns the
rendering. Both this package and the SDK task compile that source through `CohesionSharedSource`;
no helper becomes public and no shipped assembly grants internals to another. Strings and chars
use single quotes and double embedded quotes; backslashes stay literal. Numeric values use the
invariant culture, decimals keep their scale, finite floating-point values use round-trip text,
and numeric values outside the column's storage range are refused with `ArgumentException`.
The builder uses the selected member's actual CLR type even when generic inference inserts a
conversion. Unsupported CLR values and nonfinite numbers are refused before the column is added.

The renderer and `Column` compiler share the CLR storage mapping. In addition to the existing
types, chars map to String and enums map by underlying numeric range: sbyte to Int8, byte/short
to Int16, ushort/int to Int32, uint/long to Int64, and ulong to Decimal. This preserves every
enum value exactly without adding unsigned scalar column types. Temporal defaults are quoted
invariant text: DateOnly `yyyy-MM-dd`, TimeOnly `HH:mm:ss.fffffff`, DateTime
`yyyy-MM-ddTHH:mm:ss.fffffff`, DateTimeOffset the same with `zzz`, and TimeSpan signed total
seconds followed by `seconds` (#1422). Decimal tick arithmetic preserves the full TimeSpan range
and every 100 ns tick, so these typed defaults use the same PostgreSQL input grammar as CAST and
plain-string INTERVAL assignment; no shared parser dependency or visibility grant is needed.
DateTime keeps wall-clock ticks without its CLR kind, matching SQL TIMESTAMP semantics;
DateTimeOffset keeps its written offset. Guid uses the lowercase `D` form. Binary values use
quoted `\x` followed by uppercase hexadecimal bytes, which the SQL DEFAULT binder decodes.
These formats do not depend on the current culture or local time zone.

`table.DefaultSql(selector, sqlLiteral)` is the explicit raw-SQL escape hatch; the old raw
`Default(selector, string)` overload is removed with no shim. The raw text is kept as written
in `CompiledSchemaColumn.DefaultLiteral`. This package requires nonblank text; the SQL engine
validates exactly one supported literal and its column bounds before touching any file.
Expressions and function calls remain unsupported. Custom CLR domain structs have no implicit
value conversion; declare their defaults through `DefaultSql`. Both verbs compose with
`Column(selector, sqlType)`, a domain, a key and an index without changing column order.
Repeating the same rendered literal is harmless; two different literals fail at the second call.
The SDK extracts compile-time constants for `Default` and invokes the same renderer. A value it
cannot evaluate produces a coded diagnostic naming `DefaultSql`; raw SQL must still be a
constant string. Runtime values such as constructed dates, GUIDs and binary arrays are valid
for retained declarations, while source-extracted schemas express those values with `DefaultSql`.

Typed defaults and raw defaults with the same rendered literal produce identical canonical
documents and hashes. Arbitrarily different raw spellings are not normalized in the document.
Only the authoring API and rendering changed in B2 round 2; #1438 later advances the format to v5.

The optional `defaultLiteral` member is omitted when null. Adding it changes the strict document
contract, so B2 introduced `cohesion/database-schema/v4` for every schema, with or without defaults.
Earlier documents are refused; no v3 compatibility reader or upgrade is provided. Provisioning
refuses their recorded documents before any hash comparison or catalog reconstruction. The SDK compiler
and migration baseline writer share the current v8 format with runtime schema compilation.
`NULL` text remains part of a declaration's identity even
though SQL gives it the same database behavior as an absent default. Adding or removing a
default changes column equivalence, so the planner carries both definitions in `AlterColumn`
instead of reporting false convergence; the current dialect still refuses that operation.
New tables and columns carry the default into their creation steps. The SQL provisioner can
therefore reconstruct columns with defaults after a crash, preserving the declaration's spelling
when it denotes the persisted literal, and plan only the remaining steps. Schema snapshots also
retain domains and their bounds, keys, unique and ordinary indexes, references and checks; SQL
owns their catalog reconstruction. Principals and grants remain explicitly unsupported.

Foreign keys and checks are executable schema metadata. The planner first creates
all tables and their indexes, then emits `AddConstraint` operations rendered as
`ALTER TABLE ... ADD CONSTRAINT`. This permits references to tables that sort later,
self-references, and cycles without temporarily disabling enforcement. Constraint
replacement emits `DropConstraint` before removing dependent columns or indexes,
then adds the replacement after structural changes. Both constraint operations have
compensating statements, and the SQL provisioner includes their definitions when
comparing or reconstructing the live catalog after a failed apply.

`CompiledSchemaConstraint.OnDelete` uses `CompiledSchemaReferentialAction.Restrict`
by default and can select `Cascade`. The default is omitted from canonical JSON,
preserving hashes of existing documents that implicitly restricted parent deletes.
The retained C# `References` builder member keeps that default. The compiled concrete model carries the optional action directly.

`UNIQUE` is represented by `CompiledSchemaIndex(IsUnique: true)`, consistently with
the SQL language and catalog. A unique index supplies both enforcement and lookup,
so a second constraint kind would duplicate its identity and persistence rules.
Foreign keys and checks remain `CompiledSchemaConstraint` values. For a check,
`Expression.CanonicalText` is SQL scalar-expression text, for example `qty > 0`;
the SQL parser and evaluator validate it at provisioning. "Canonical" here means
the schema document's canonical JSON, not SQL spelling: the text stays as the
author wrote it and feeds the hash. The SQL engine stores its own canonical SQL
rendering of the parsed predicate in the catalog and compares the live catalog with
a schema by that rendering, so reapplying an unchanged schema is a no-op whatever
the predicate's spelling. Its optional `Columns`
list is advisory and is not part of check equivalence, because table-level SQL
checks derive their dependencies from the expression.

**`table.Check(name, sql)` (E2 part 2, owner decision 64).** The retained table builder
declares a CHECK as a name and SQL text: `SqlTableBuilder<TRow>.Check(string name, string
sql)`, both non-blank. The compiler lowers it to a `CompiledSchemaConstraintKind.Check`
constraint whose `Expression.CanonicalText` is the text exactly as written and whose
`Columns` list is empty, in the `cohesion/database-schema/v2` document (no format change: the
v2 record already carried compiled checks; E3 later moved the format to `v3`, above). A CHECK's name shares the table's constraint
and index name space, as in the SQL catalog: a name another constraint, the primary key
(`PK_<table>`) or an index (`IX_<table>_<member>`) already has is `DuplicateDeclaration`.
This package does not parse SQL, so it neither validates the predicate nor knows which
functions exist: the SQL engine's build parses the text as exactly one expression and binds
it to its frozen function catalog and the table's declared columns before it touches any
file, refusing an unknown column or function, a call no overload accepts, or a function that
is not `IMMUTABLE` (`COHSQLP001`). The arguments are meant to be constant strings, so the
`Sdk.Database` build task extracts the same constraint without running the program and its
document and hash equal the runtime compiler's.

**Concrete types (concrete-types plan, phase 4, §6.7).** Until phase 4 the declaration
surface was fifteen public interfaces: `ISqlSchema`, the four builder contracts
(`ISqlSchemaBuilder`, `ISqlTableBuilder<TRow>`, `ISqlTypeBuilder`,
`ISqlPrincipalBuilder`), the trigger context `ISqlTriggerContext`, and nine declaration
contracts (`ISqlSchemaTable`, `ISqlSchemaType`, `ISqlSchemaColumn` and their siblings)
over internal positional records; `SqlSchema` and `SqlSchemaCompiler` were public static
classes. Each had one implementation, and only this package's tests read the declaration
contracts. The builders and the trigger context are now public sealed classes (the
builders with internal constructors, the trigger context with a private one: it is a
phantom that appears only inside trigger expression trees, and its `Audit` member never
runs). `SqlSchema` is an opaque public sealed class with an internal constructor, a
`Name` and an instance `Compile()`; the static `Create(name, configure)` and
`Compile(name, configure)` keep their names. The nine records stay internal
(`Internal/SqlSchemaDeclaration.cs`, `Internal/SqlSchemaType.cs`, `Internal/SqlSchemaTable.cs`, `Internal/SqlSchemaReference.cs`, `Internal/SqlSchemaCheck.cs`, `Internal/SqlSchemaColumn.cs`, `Internal/SqlSchemaPrincipal.cs`, and `Internal/SqlSchemaGrant.cs`), because a public positional record cannot close its
primary constructor or its `with` clone, and `SqlSchemaCompiler` is internal; the tests
reach both through `[InternalsVisibleTo("Assimalign.Cohesion.Database.Sql.Schema.Tests")]`
(`src/Properties/AssemblyInfo.cs`, the package's first grant). The `Sdk.Database`
extractor and canonicalizer match the builders and the trigger context by metadata name
(`CSharpSchemaExtractor.cs`, `CSharpExpressionCanonicalizer.cs`), so their strings changed
in the same commit; the SDK's parity test, which declares a type, a table with a
reference, a function, a trigger and a principal, compiles the same document both ways.
Two observable changes: the trigger context's type identity is part of a trigger's
canonical expression text, so the compiled hash of a schema that declares a trigger
changed with the rename (nothing has shipped), and `Table<T>(name, …)` and
`Extension(name, …)` report a blank name with the parameter name `name`, which the former
implementation reported as `tableName` and `extensionName`. (B1 later deleted the trigger
context, the trigger and function declarations and `Extension`, and with them the SDK's
canonicalizer; the SDK's parity test now declares types, tables and a principal; see above.)

The package targets `net10.0`, `LangVersion=Preview`, and is AOT-compatible.
The compiler reads statically supplied expression nodes and their type metadata;
it does not discover or invoke members with reflection, compile expressions,
scan assemblies, or activate code. Method signatures are derived from the
expression's argument and result nodes. Version-free type identities are parsed
from the statically supplied assembly-qualified identity string, preserving nested
generic arguments and array suffixes without reflective type discovery.
Co-located Shouldly tests cover the
retained declarations, canonical documents, ownership, validation, and migration
planning.

## Diagnostics

No EventSource (batch D part 1, #1349, reviewed 2026-10-11). Compilation also runs in application
composition through `SqlSchema.Compile`; canonical serialization, validation and migration planning
are in-memory transformations callable at runtime. `SqlCompiledSchemaSerializer.Read`/`ReadAsync`
and `Write`/`WriteAsync` do file I/O, including creating the destination directory on writes. Their
I/O and cancellation failures propagate to the caller unchanged; malformed JSON becomes a
`SqlSchemaValidationException` retaining the original exception. These caller-invoked helpers own
no service or background work and swallow no operational failure. Their caller owns failure
reporting, so they do not warrant a separate source. Database.Sql owns schema application and its
lifecycle events; SDK compilation reports through MSBuild's task log. This decision accounts for
runtime compilation and file helpers, rather than treating this package as build-time only or
I/O-free.

## E4 scripts

SQL CREATE SCHEMA (#1030) creates a SQL namespace, a different concept from this package's
compiled schema. Builders and compiled declarations remain pinned to dbo, with no namespace
targeting option. The runtime persists dbo when creating a database and refuses DROP SCHEMA dbo;
creating another namespace does not redirect code-first objects. Shared SqlNamespaceNames.Default
keeps runtime provisioning and SDK migration rendering on the same default. Namespace records
and their SQL ownership are catalog metadata and add no members to the compiled document.

SqlDatabaseBuilder.Script belongs to the database declaration after typed apply. SqlCompiledSchema
uses v8 and typed defaults remain literal-only. Script history has its own ordered-set digest
and catalog-generation marker, so unchanged reconciliation hashes nothing. Script-created tables
and indexes are excluded from typed snapshot reconstruction; deleting script code undoes nothing.
See Database.Sql's E4 section for recovery and the inline-execution/SDK owner-review decisions.

## Modifier validation (#1438)

The builders and compiled-schema validator link Sql.Language/shared/SqlTypeModifiers through
CohesionSharedSource, as does Sdk.Database. The helper owns the built-in mapping, modifier ranges
and argument splitter; it is stateless, internal and reflection-free. No Language assembly
reference or public bridge is needed. Column(selector, sqlType) refuses an invalid modifier
immediately with SqlSchemaValidationException, UnsupportedType, naming table.column.
SqlTypeBuilder.Decimal uses the same exception and names the domain (the CLR type's simple name).
Its previous ArgumentOutOfRangeException becomes the schema's existing coded error category.
CompiledSchemaColumn fields and CustomType text, and CompiledSchemaType precision/scale, are
validated when a SqlCompiledSchema is constructed or deserialized, closing the raw-record path.

Precision is 1..28, scale 0..precision (an omitted scale means zero with an explicit precision),
and explicit string/binary lengths are 1..10485760; omitted bounds remain unbounded. Domains take
no modifiers at use sites. Typed names are identifiers or closed built-in multi-word names; interval field qualifiers are
also recognized solely to report COHDBL001. Integer arguments
now use the parser's Int32 rule rather than the previous nine-digit shape check. DIALECT.md is the
range contract. C# int/string parameters and the raw compiled
records cannot express these ranges at compile time; runtime declaration validation and the SDK's
COHDBSDK106 provide parity without new public signatures. COHSQLS is the engine's script-history
family, not this package's validation codes.

Compiled table and schema snapshots normalize raw decimal columns and domains with a precision
but no scale to scale zero, including records made with `with` and deserialized documents.
The shared `NormalizeScale` rule also serves typed builders, the validator, SQL resolution,
provisioning and SDK extraction. An omitted scale and explicit zero therefore have the same
canonical document and hash; a bare decimal keeps both fields null.

The current format is `cohesion/database-schema/v8`, shared with Sdk.Database. Although the JSON
members are unchanged, Character identity distinguishes v6 from v5; decimal normalization
already changed the identity of a v4 raw NUMERIC(p) column whose
precision was set and scale was null. Deserialization checks the format before constructing any
schema objects. Earlier formats fail with SqlSchemaValidationException, InvalidDocument at
schema.format, naming the found/current formats and the remedy: rebuild artifacts and recreate
affected databases. Applied markers and pending targets retain the document's format, so SQL
gates them at open and before Apply/Verify compares any hashes. No old hash is validated or
reconciled. SQL record-space format 13 separately gates the alias vocabulary and CHAR keys; there is no upgrade path.

SQL type text accepts FLOAT(p) for 1..53 bits. The engine resolves 1..24 as Float32 and 25..53 as
Float64 and verifies that it matches the member's CLR storage; the compiled declaration retains
its SQL text. Temporal precision is recognized but unsupported: the existing UnsupportedType
exception carries COHDBL001 and names the table/column and modifier, for builders and raw compiled
CustomType metadata alike. Invalid FLOAT precision retains SQL0005 in the validation detail.

INTERVAL field qualifiers use the shared PostgreSQL field/range grammar, including SECOND(p).
Builders and raw CustomType metadata report UnsupportedType with COHDBL001 naming table/column
and the full qualifier, with or without precision. Field restrictions and rounding do not execute.

## Migration diagnostics (#1103)

SERIAL, BIGSERIAL and SMALLSERIAL cannot name declared custom types: the shared rejected-type
rule reports COHDBL001 with GENERATED BY DEFAULT AS IDENTITY when constructing or compiling
the schema, before an unreachable domain can be provisioned.

A migration exception with a coded DatabaseException cause preserves that cause's complete
structured Diagnostics list. The migration message adds application context; callers read
the stable code and source span from the carrier.

## Temporal default text and schema identity (#1422)

The canonical document advances from v7 to v8 with SQL catalog format 26. TimeSpan defaults
now render as exact signed seconds, including TimeSpan.MinValue, instead of the CLR day-dot-clock
spelling. This changes DefaultLiteral and therefore Hash for the same C# declaration.
An existing application must rebuild its compiled artifact; applying or verifying an earlier
artifact, applied marker or pending target is refused by InvalidDocument at schema.format
before hashes are compared. Opening an earlier catalog is refused too: export with its writing
engine, recreate, and reapply the rebuilt declaration. There is no upgrade or compatibility
reader (#1152). Raw defaults and their typed equivalents share the v8 document and hash.
