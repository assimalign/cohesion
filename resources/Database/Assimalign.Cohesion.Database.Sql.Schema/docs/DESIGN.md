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

`SqlSchema.Compile` is the ordinary composition-root entry point: it declares and
compiles a SQL schema without making the caller repeat `EngineModel.Sql`. It
delegates to the same `SqlSchema.Create` and `SqlSchemaCompiler` path, so validation,
canonical documents, and hashes remain identical. `Create` still returns an
`ISqlSchema`, built by internal implementations of `ISqlSchemaBuilder` and the
table/type/principal builder contracts, for build tooling and callers that compile
a declaration they did not author. `SqlSchemaCompiler` accepts only `EngineModel.Sql`
and lowers that retained C# declaration into `SqlCompiledSchema`. The derived type carries SQL tables,
columns, keys, indexes, constraints, types, functions, triggers, principals,
grants, and extensions. The root `CompiledSchema` carries only identity and a
canonical document, with SHA-256 hashing shared across models.

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
Validation rejects malformed input, unknown JSON members, incompatible models,
duplicate declarations, missing references, and unsupported value types.

`SqlSchemaMigrationPlanner` produces deterministic dependency-ordered operations
and requires the declaration's destructive-change opt-in for data-losing steps.
Unsupported metadata changes fail explicitly. The model-independent apply
contract and `SchemaMigrationResult` stay in the root.

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
