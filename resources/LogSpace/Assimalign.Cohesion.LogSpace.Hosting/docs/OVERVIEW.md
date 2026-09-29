# LogSpace Hosting overview

LogSpaceApplication.CreateBuilder(args).Build() composes the ambient resource. An enabled otlp endpoint registers the receiver and a dedicated segment flush service. query hosts management and GET /cohesion/v1/logs; a plain unregistered host remains an ordered collection of explicit services. SinkHost demonstrates the ambient contract.

POST /v1/logs requires application/json and a signed ES256 telemetry token. Query accepts resource, since (ISO-8601), limit (default 100/max 1000), and cursor, returning application/x-ndjson and X-Cohesion-Next-Cursor when more scanning is possible. Query pages are bounded to four million characters (plus at most one bounded record). Query and management require the sink's ordinary bootstrap/dev credential; telemetry scope is rejected.

## Concrete composition (T10 / O34)

`LogSpaceApplication.CreateBuilder(args)` returns the public concrete `LogSpaceApplicationBuilder`; its `Build()` returns the public `LogSpaceApplication : Host<LogSpaceApplicationContext>`. The public `LogSpaceApplicationContext` implements `ILogSpaceApplicationContext`, reading `ContentRootPath` from the host environment. The application explicitly forwards the root lifecycle contract to `IHost`, and consumers use the concrete application for `RunAsync` and `await using`. Runtime options and supporting services remain internal.

Background-work registration belongs to the concrete `LogSpaceApplicationBuilder`: `AddService(IHostService)` and `AddService(Func<LogSpaceApplicationContext, IHostService>)`. The factory receives the same concrete context as Web's and Database's AddService, so hosting consumers can use environment, state, and hosted-service members beyond the small root contract. Database's root-level AddServer keeps the interface context. Factories run once per build against the same context retained by the application; the hosted-service snapshot is installed after factory evaluation. Services start in registration order and stop in reverse. No area-owned service abstraction is introduced.
