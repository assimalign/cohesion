# TCP SDK acceptance fixture

A normal `Sdk.Database` executable composes KeyValuePair, Graph and Blob servers through the
shared public `Listen(Uri)` extension. It has no project or package references and uses the
SDK's framework packs. ApplicationModel generation and schema compilation are disabled because
this fixture tests transport composition rather than the existing SQL manifest/schema fixture.

Database.Testing launches the self-contained process, waits for its explicit READY notification,
authenticates with each model family and requires Pong. STOP requests normal application shutdown.
The evaluated compiler-reference artifact proves Database.Tcp is public while Connections.Tcp
is absent from compile references and present beside the executable at runtime.

See [DESIGN.md](DESIGN.md) for ownership, readiness and build prerequisites.

All three fixture engines retain their default in-memory storage: no RootPath or storage
strategy is configured, so the process creates no persistent data directory to clean up.
