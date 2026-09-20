# SqlDatabaseServerOptionsExtensions

`SqlDatabaseServerOptions.Listen(Uri)` configures the options with a TCP listener
and returns the same options for fluent composition. Reference Database.Sql.Tcp
and import Assimalign.Cohesion.Database.Sql. The composition root owns the listener.

The URI must be an endpoint URI with a port. Accepted bind hosts are IP literals,
localhost (IPv4 loopback), IPv4 wildcards, and IPv6 any. DNS lookup is not performed.
Null options/URI throw ArgumentNullException, malformed endpoints throw
ArgumentException, and unsupported hosts throw InvalidOperationException.
