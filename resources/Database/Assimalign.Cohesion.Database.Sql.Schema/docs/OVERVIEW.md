# Assimalign.Cohesion.Database.Sql.Schema — Overview

SQL's thin schema package provides `SqlSchema.Create`, the `ISqlSchema*`
declaration contracts, `SqlSchemaCompiler`, `SqlCompiledSchema`, its canonical
serializer, and SQL migration planning. Both SDK build tooling and the SQL engine
consume it without moving relational vocabulary into the Database area root.

Compiled tables, indexes, and constraints are schema-owned. The SQL catalog
persists that ownership and the engine protects those objects from session DDL.

See [DESIGN.md](DESIGN.md) for boundaries, canonicalization, and AOT decisions.
