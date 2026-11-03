# Database TCP composition design

## Named platform endpoints (#1790, EN-A1)

Server creation snapshots options before validation. When that snapshot has no Listener,
DatabaseServerOptions reads the current AppEnvironment scope with TryGetEndpoint: SQL uses sql,
Documents documents, Graph graph, KeyValuePair kv, and Blob blob. An explicit Listener, including
Listen(Uri), wins without reading the platform endpoint. Missing or malformed contract values
retain CK08001. A DNS bind host retains CK07101. No endpoint is invented and no server is added.
Custom model names have no platform fallback. Without assigned values, unmanaged behavior is unchanged.

This follows Web.Hosting's HttpServerConfiguration.GetPlatformEndPoint
(resources/Web/Assimalign.Cohesion.Web.Hosting/src/Internal/HttpServerConfiguration.cs:853-874):
read the scoped runtime contract, not process environment variables or configuration. The root
privately references Connections.Tcp; the existing App.Database runtime-private membership already
carries it. Models keep their generic Connections surface and gain no transport reference, public
member or hosting dependency. The common stateless binder lives in Database/shared/EndpointBinding,
compiled by Database and Database.Tcp, preserving Listen's IdnHost/literal-address policy.
As with Listen(Uri), a scheme is endpoint metadata, not transport selection: this default is plain
TCP and does not configure TLS. A secured or different transport needs an explicit Listener.
The listener is inert until server start; the server owns its bind, drain and terminal disposal.
The caller's original options remain unchanged and can be reused in another environment scope.
TCP construction is behind a non-inlined internal factory, so generic-listener and missing-listener
paths remain usable without deploying TCP.

The in-process platform currently assigns only http. Five-model tests assign the named runtime
values directly and complete handshake/Ping, including under NativeAOT; named platform allocation
and per-endpoint outputs remain ApplicationModel escalation E-AM1. No wire or store format changes.

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
loopback, wildcard -> Any; DNS names are refused synchronously with CK07101. Endpoint URIs
must be absolute, have a host and explicit positive port, and have no user information, query or
fragment. Port zero remains invalid for a resolved endpoint URI; transport-level ephemeral binds
remain available through the generic listener option. The old Sql.Tcp design's port-zero claim
was inconsistent with Uri.ThrowIfNotEndpoint and is not carried forward.

Missing Listener, invalid MaxSessions and invalid timers report model, engine and the exact
option as ParamName, with kernel resx codes CK08001, CK08002 and CK08003. Existing timer
policies are retained: Documents permits zero authentication/idle values; Blob requires positive
values; both accept InfiniteTimeSpan. SQL/Graph/KeyValue retain their existing authentication/
idle policy. All models require positive finite shutdown/linger values no greater than
4,294,967,294 milliseconds. Configuration ArgumentExceptions have no SQLSTATE. No wire,
protocol, storage format, event or counter changes.

The TCP address setup was compared with PostgreSQL src/backend/libpq/pqcomm.c:442-470
(AI_PASSIVE and pg_getaddrinfo_all) in the local clone. Cohesion deliberately keeps its existing
literal-only bind policy instead of copying hostname resolution. No reference-engine code is copied.

Listener extension containers are partial and compiler/AOT defaults are inherited from the shared build. The generic Listen signature, returned concrete option type, URI checks and listener lifecycle are unchanged.


## Framework membership validation

The framework-closure guard reads the Runtime producer's public, runtime-private and analyzer item groups as one logical membership list. Splitting public and private items into separate unconditioned groups follows the repository's one-item-kind-per-group rule; it does not change which assemblies ship or become compiler references. The loader retains document order and validates supported kinds, unconditional membership, nonempty unique identities and the public umbrella first. Existing project/package closure and transitive visibility checks still run across every area.

The guard accepts existing single-group area lists without rewriting other areas. New multi-group lists must keep each group homogeneous. Malformed-list and visibility fixtures plus omission, duplicate and private-edge mutants protect this aggregation from hiding missing or exposed implementation dependencies.


## Delivery and acceptance (#1391)

The standalone release package, Database CI matrix and repository solutions use Database.Tcp;
the deleted Database.Sql.Tcp is no longer a delivery entry. SDK smoke watches this package. The
Linux NativeAOT job publishes and runs the five-model guard and requires its completed handshake/
Ping result without compiler or trimming warnings. Database.Testing also builds an SDK-only
consumer against installed framework packs: KeyValuePair, Graph and Blob compose Listen(Uri),
authenticate and answer Ping. Its evaluated compiler references include Database.Tcp and exclude
Connections.Tcp, while the latter remains present in the runtime closure. No SQL grammar, wire
message, storage format, public API or error-code change is needed for this delivery completion.


## Native function dispatch canary

After the five-model handshake/Ping checks, the TCP NativeAOT sample registers a typed delegate
and a protected-core key-value function on an engine builder. A real TCP connection invokes both
through parameterized CALL over the existing Execute/result frames and checks each Int64 result
and its metadata. This covers application callback dispatch in a trimmed native server without
adding a wire message, protocol version or project reference.
