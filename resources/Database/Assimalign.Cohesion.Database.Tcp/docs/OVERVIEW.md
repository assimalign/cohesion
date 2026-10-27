# Database TCP composition

Reference Assimalign.Cohesion.Database.Tcp and import Assimalign.Cohesion.Database to call
options.Listen(endpoint) on SQL, Documents, Graph, Blob or KeyValue server options. The generic
extension returns the concrete options type. App.Database includes this package publicly;
Connections.Tcp remains an implementation dependency. No engine selects a transport itself.

Use an absolute resolved endpoint URI with an explicit positive port. IP literals, localhost and
wildcards bind directly; DNS hosts and port zero are refused. The server owns the listener after
successful composition. Stop disposes it terminally. See DESIGN.md for validation and ownership.

Listener extension containers are partial and compiler/AOT defaults are inherited from the shared build. The generic Listen signature, returned concrete option type, URI checks and listener lifecycle are unchanged.


Framework validation combines the public and runtime-private membership groups before checking dependencies and compiler visibility. Database.Tcp remains framework-public, while Connections.Tcp remains runtime-private. Separate item groups do not change that contract.


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
