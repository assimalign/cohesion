# Assimalign.Cohesion.Database.Sql.Schema — Design

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

**Standalone since B1** (owner decisions 50, 57 and 59 of 2026-10-09,
`docs/programs/DATABASE_ENGINE_EXTENSIBILITY_DESIGN.md`). `SqlCompiledSchema` used to derive the
area root's model-agnostic `CompiledSchema`, which carried an engine `Model` and recomputed the
canonical document and its SHA-256 on every read (one apply read each twice). The root keeps no
schema type now; the compiled schema owns its `Format`, `Name` and `AllowsDestructiveChanges`, and
computes `CanonicalDocument` and `Hash` once, on first read (the schema is immutable, so a race on
the first read costs only duplicate work). Dropping `Model` changed the document, so the format is
`cohesion/database-schema/v2`; a `v1` document no longer deserializes (the format check refuses it,
and its `model` member is unmapped), and a database whose recorded state is `v1` is replanned from
its catalog snapshot. The root's `SchemaMigrationResult` moved here as `SqlSchemaMigrationResult`.
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

`SqlSchemaMigrationPlanner` produces deterministic dependency-ordered operations
and requires the declaration's destructive-change opt-in for data-losing steps.
Unsupported metadata changes (custom types, principals) fail explicitly. SQL identifiers compare
without case. Applying a plan is the SQL engine's (`Database.Sql`), which reports the outcome as a
`SqlSchemaMigrationResult` (`FromHash`, `ToHash`, `OperationCount`, `WasAlreadyApplied`).

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
checks derive their dependencies from the expression. The frozen
retained table builder has no check declaration member yet (`table.Check(name, sql)` arrives in
E2); callers construct the compiled check model directly.

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
(`Internal/SqlSchemaDeclaration.cs`), because a public positional record cannot close its
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
