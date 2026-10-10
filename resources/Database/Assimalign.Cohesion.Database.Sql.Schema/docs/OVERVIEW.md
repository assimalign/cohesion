# Assimalign.Cohesion.Database.Sql.Schema — Overview

SQL's thin schema package provides one-step `SqlSchema.Compile`, the lower-level
`SqlSchema.Create` with the opaque declaration it returns and its own `Compile()`, the
sealed declaration builders, the standalone `SqlCompiledSchema` (format
`cohesion/database-schema/v2`, its canonical document and SHA-256 computed once), its canonical
serializer, SQL migration planning and `SqlSchemaMigrationResult`. Both SDK build tooling and the
SQL engine consume it; the Database area root holds no schema type.

Compiled tables, indexes, and constraints are schema-owned. The SQL catalog
persists that ownership and the engine protects those objects from session DDL.

A table declares a CHECK with `table.Check(name, sql)`: the SQL text is kept as written in
the document and hash, and the SQL engine's build binds it to its registered functions
before it touches a file. Pass constant strings, so the SDK can extract it.

See [DESIGN.md](DESIGN.md) for boundaries, canonicalization, and AOT decisions.
