# Database TCP composition design

## Shared server options and TCP composition (#1391, D7 B)

The five sealed model server-options types derive from root DatabaseServerOptions. The base owns
Listener, Authenticator, MaxSessions, AuthenticationTimeout, IdleTimeout and ShutdownDrainTimeout.
Defaults stay 100 sessions, ten seconds for authentication, thirty minutes idle and thirty seconds
for shutdown. TerminalDrainLinger remains on the model leaves, default two seconds. Each model
still snapshots every value before validation and owns the transferred listener through stop.
No public Database interface is added; the count remains five.

Database.Tcp replaces Database.Sql.Tcp. Its root-namespace extension
Listen<TOptions>(this TOptions options, Uri endpoint) where TOptions : DatabaseServerOptions
returns that same concrete options instance. AddServer(configure) and AddServer(factory) retain
their existing ownership rules. Engines still reference generic Connections, never the TCP
integration. App.Database publicly carries Database.Tcp; Connections.Tcp stays runtime-private.
Consumer source migrates its package reference and imports Assimalign.Cohesion.Database.

Binding preserves the previous SQL helper: Uri.IdnHost, literal IPv4/IPv6, localhost -> IPv4
loopback, wildcard -> Any; DNS names are refused synchronously with COHDBTCP001. Endpoint URIs
must be absolute, have a host and explicit positive port, and have no user information, query or
fragment. Port zero remains invalid for a resolved endpoint URI; transport-level ephemeral binds
remain available through the generic listener option. The old Sql.Tcp design's port-zero claim
was inconsistent with Uri.ThrowIfNotEndpoint and is not carried forward.

Missing Listener, invalid MaxSessions and invalid timers report model, engine and the exact
option as ParamName, with kernel resx codes COHDBO001, COHDBO002 and COHDBO003. Existing timer
policies are retained: Documents permits zero authentication/idle values; Blob requires positive
values; both accept InfiniteTimeSpan. SQL/Graph/KeyValue retain their existing authentication/
idle policy. All models require positive finite shutdown/linger values no greater than
4,294,967,294 milliseconds. Configuration ArgumentExceptions have no SQLSTATE. No wire,
protocol, storage format, event or counter changes.

The TCP address setup was compared with PostgreSQL src/backend/libpq/pqcomm.c:442-470
(AI_PASSIVE and pg_getaddrinfo_all) in the local clone. Cohesion deliberately keeps its existing
literal-only bind policy instead of copying hostname resolution. No reference-engine code is copied.
