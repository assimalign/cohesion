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
