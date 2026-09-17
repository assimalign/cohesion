# Database sample host fixture

This non-packable acceptance fixture composes the SQL engine and server in its
ordinary `Program.cs`. `SqlSchema.Create` retains the SQL declaration for both
SDK build-time analysis and runtime `SqlSchemaCompiler` compilation. Hosting
receives the resulting model-agnostic `CompiledSchema` through `AddDatabase` and
provisions it before the server accepts connections.

The fixture is owned by `Database.Testing`; its tests exercise the generated
resource manifest, control plane, SQL round trips, and restart durability.
