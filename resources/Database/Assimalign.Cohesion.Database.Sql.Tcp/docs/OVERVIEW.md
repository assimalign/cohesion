# SQL TCP composition

Reference this package when the composition root chooses TCP for a SQL endpoint.
It provides the existing `SqlDatabaseServerOptions.Listen(Uri)` extension in the
`Assimalign.Cohesion.Database.Sql` namespace. The engine accepts only the generic
`IConnectionListener`; this integration constructs a `TcpConnectionListener`.

See [design](DESIGN.md).
