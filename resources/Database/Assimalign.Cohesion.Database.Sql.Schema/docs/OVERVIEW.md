# Assimalign.Cohesion.Database.Sql.Schema — Overview

SQL's thin schema package provides one-step `SqlSchema.Compile`, the lower-level
`SqlSchema.Create` with the opaque declaration it returns and its own `Compile()`, the
sealed declaration builders, the standalone `SqlCompiledSchema` (format
`cohesion/database-schema/v5`, its canonical document and SHA-256 computed once), its canonical
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
`table.DefaultSql(selector, "'pending'")` supplies raw SQL literal text as an escape hatch.
The compiled column keeps the rendered text in `DefaultLiteral`, so equivalent typed and raw
declarations have identical documents and hashes; the format remains v4. The SDK uses the same
renderer for C# compile-time constants and directs nonconstant declarations to `DefaultSql`.
The SQL engine validates defaults before provisioning, and they survive canonical documents
and catalog reconstruction after an interrupted migration.

See [DESIGN.md](DESIGN.md) for boundaries, canonicalization, and AOT decisions.
