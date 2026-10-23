# Database TCP composition

Reference Assimalign.Cohesion.Database.Tcp and import Assimalign.Cohesion.Database to call
options.Listen(endpoint) on SQL, Documents, Graph, Blob or KeyValue server options. The generic
extension returns the concrete options type. App.Database includes this package publicly;
Connections.Tcp remains an implementation dependency. No engine selects a transport itself.

Use an absolute resolved endpoint URI with an explicit positive port. IP literals, localhost and
wildcards bind directly; DNS hosts and port zero are refused. The server owns the listener after
successful composition. Stop disposes it terminally. See DESIGN.md for validation and ownership.
