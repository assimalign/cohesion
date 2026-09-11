# Database SDK Design

## Boundaries

The consumer's C# `AddDatabase(engine, name, schema => ...)` declaration is the only schema
source. The SDK compiler reads Roslyn syntax and symbols from `@(Compile)` and
`@(ReferencePath)`; it never invokes `Program.Main`, starts an engine, loads a consumer plug-in,
or scans `Schema/**/*.sql`. The compiler lowers that declaration into the public Database root's
`CompiledSchema` contract and uses `CompiledSchemaSerializer` for the canonical document and
hash. This keeps build, migration, and provisioning artifacts on one contract.

The SDK is a build-time layer. Its tasks may run under the JIT-based MSBuild host and reference
the AOT-compatible Database root, but the task and Roslyn assemblies never enter the consumer's
runtime closure.

## Orchestration commands

When `CohesionApplicationModel=enabled`, the base manifest task writes this SDK's
`CohesionCommand` defaults, `database.add-database` and `database.add-principal`, as
bare strings in `commands`. These advertise the Database default control plane's
bounded command set. Typed declarations ship in Database.ApplicationModel and are
delivered through Database.Client; the SDK never executes them or places payloads in
the manifest. The schema compiler and migration path remain independent.

## Model selection

`CohesionDatabaseModel` is an exact selector. The shared targets file contains explicit,
allowlisted imports:

- `Sql` imports `Sdk.Database.Sql.targets`.
- `KeyValuePair` imports `Sdk.Database.KeyValuePair.targets`.

Each imported file sets `_CohesionDatabaseModelTargetsLoaded` and registers one private compile
target through `_CohesionDatabaseModelCompileTarget`. An absent selector fails with
`COHDBSDK001`; an incomplete model file fails with `COHDBSDK002`. Explicit imports are deliberate:
a project property can never expand into an arbitrary import path.

## Build contract

`CohesionDatabaseCompileSchema` runs after references resolve and before `CoreCompile` when
`CohesionDatabaseProject=true`. Model targets pass the same C# inputs to
`CompileDatabaseSchemaTask` and differ only in their fixed model identity. The task accepts only
statically analyzable schema declarations: names and numeric configuration are compile-time
constants, selectors are direct members, and schema callbacks cannot depend on captured runtime
state. One SDK artifact must contain exactly one `AddDatabase(engine, name, schema)` declaration;
zero or multiple declarations fail because the current output contract represents one logical
database. Unsupported code produces a named file/line diagnostic and fails the build.

The outputs are:

| Property | Default | Meaning |
| --- | --- | --- |
| `CohesionDatabaseSchemaOutputPath` | `$(IntermediateOutputPath)cohesion/database.schema.json` | Canonical `CompiledSchema` JSON. |
| `CohesionDatabaseSchemaHashOutputPath` | `$(IntermediateOutputPath)cohesion/database.schema.sha256` | Lowercase SHA-256 of the exact canonical UTF-8 document bytes. |
| `CohesionDatabaseSchemaHash` | Task output | The same hash for downstream targets in the current build. |

The document contains semantic data only—never source paths or timestamps. The task writes with
LF line endings and avoids replacing unchanged outputs. Both files are target `Outputs` and
`FileWrites`; editing C# or a reference assembly invalidates the target, and `Clean` removes them.
If validation fails, neither final artifact is replaced.

## Migrations

`CohesionDatabaseCreateMigration` first compiles the desired schema. A required, sanitized
`CohesionDatabaseMigrationName` is combined with the next four-digit ordinal. SQL compares the
new document with the newest `NNNN_name.schema.json` baseline through the shared
`SchemaMigrationPlanner`, renders operations in planner order, and atomically emits matching
`NNNN_name.sql` and `NNNN_name.schema.json` files. There are no timestamps. Missing names,
invalid schemas, unsupported/destructive operations, and path collisions fail without partial
outputs. `KeyValuePair` fails explicitly because a SQL script is not a valid migration artifact
for that model. SQL text uses the same guarded, `dbo`-qualified dialect as the runtime renderer;
the current dialect explicitly rejects constraints, custom types, ALTER metadata, and required
column additions that have no default or backfill.

## Verification

SDK-local tests evaluate the real target imports for SQL and KeyValuePair and verify the fallback
diagnostic for unknown/case-mismatched selectors. Task tests cover static/runtime canonical JSON and
hash parity, invalid C# schema diagnostics, SQL migration numbering/dialect output, and the
KeyValuePair migration error. Packaging remains governed by the repository's SDK pack contract;
Microsoft.Build host assemblies are excluded from the shipped task dependency closure.
