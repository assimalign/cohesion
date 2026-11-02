# LogSpace Hosting design

## Platform sink and boundaries

The area's resource integration is `Assimalign.Cohesion.LogSpace.Hosting.ApplicationModel` (`builder.Services.AddResource(...)`), which references this module; this module references no application-model library. The resource root remains contract-only; all composition is in Hosting. The builder registers only the application context and host environment: the internal OTLP receiver (`OtlpReceiverEndpointService`), its segment store and flush service, and the query middleware exist, but nothing registers them, so a built LogSpace application opens no listener. They stay unregistered on purpose: the receiver has no access control, and an access decision comes before any registration. OTLP/HTTP JSON logs are accepted and protobuf receives 415 with an OTLP/JSON-only detail; /v1/traces and /v1/metrics are reserved POST routes returning 501 after content validation: libraries/Logging has no span or instrument primitive, and protobuf is deferred. The HTTP server has no gRPC framing (response trailers ship on HTTP/2 and HTTP/3 since decision 18, but gRPC hosting stays outside the HTTP/Web program), so the receiver serves OTLP over HTTPS and requires an `https` endpoint.

## HTTP and TLS

Middleware checks known path, method and application/json content; the OTLP routes check no caller identity. Wrong method yields 405/Allow: POST, unknown path 404, malformed JSON 400, oversized body 413 and unavailable storage 503. Http.RequestLimits is a private reference. LogSpace owns the one-MiB Content-Length check and bounded streaming read, returning 413 with Connection: close. The listener body cap is disabled because its post-dispatch Http1LimitExceededException currently closes the connection without a 413; header and data-rate transport limits remain in force. No HTTP-area code was changed. JSON uses a depth bound and at most 8192 records per request. Full acceptance returns {"partialSuccess":{}}; queue overflow returns rejectedLogRecords as an int64 string. Traces/metrics have no acceptance path.

## Query access and middleware order

The query middleware permits `/cohesion/v1` and every path below it
only when the exchange's transport `RemoteEndPoint` is `127.0.0.1`, `::1`, or IPv4-mapped
`127.0.0.1`. It performs this check before serving `/cohesion/v1/logs`. Missing and non-IP endpoints fail closed;
forwarded headers never grant access. Other peers receive `403 Forbidden`, chosen to make the
development-only access restriction clear. This is a breaking security change from 10.0.0-preview.1, which admitted remote
standalone query and management requests (#1431, owner decision 46; preview compatibility follows
decision 15).

## Storage, lifecycle and query

Receiver threads enqueue immutable records to a channel bounded to 8192 entries and a conservative 64 MiB estimated record budget. SegmentFlushService owns synchronous writes and fsync on its dedicated thread, every 250 ms and on stop; receivers stop before the flush service. Files are append-only logs-YYYYMMDD-NNNN.ndjson, rotated daily or at 16 MiB; each restart opens a fresh lexically ordered segment. Records have t (nanosecond string), sev, sevText, svc, cat, body and attrs. A per-segment .index records first/last timestamps and count; the minimal query currently scans segments instead of relying on this sidecar. IOException/UnauthorizedAccess marks the store unavailable, retaining queued entries for a subsequent flush and yielding 503 rather than stopping the host. Retention and crash-tail repair remain future work.

Queries filter service.name and timestamp, cap limit to 1000, and return bounded NDJSON. Base64 cursors carry segment/line position and a filter fingerprint, with path validation; they grant no authorization. A cursor on the final full page may yield an empty next page. Producer-controlled JSON uses manual JsonDocument validation; stored records and cursors use source-generated LogSpaceJsonContext, without reflection serialization.

R-5 explicitly defers the developer-experience design §8 Database.Embedded storage requirement to the Database MVP embedded-consumption phase; this segment implementation is not a replacement architectural decision. No cross-area Database reference was added.

## Non-goals

LogSpace.Telemetry remains empty project scaffolding. OTLP access control, inferred telemetry dependencies, retention/archival, protobuf, gRPC, traces and metrics are separate deliverables.

## Concrete composition (T10 / O34)

`LogSpaceApplication.CreateBuilder(args)` returns the public concrete `LogSpaceApplicationBuilder`; its `Build()` returns the public `LogSpaceApplication : Host<LogSpaceApplicationContext>`. The public `LogSpaceApplicationContext` implements `ILogSpaceApplicationContext`, reading `ContentRootPath` from the host environment. The application explicitly forwards the root lifecycle contract to `IHost`, and consumers use the concrete application for `RunAsync` and `await using`. Runtime options and supporting services remain internal.

Background-work registration belongs to the concrete `LogSpaceApplicationBuilder`: `AddService(IHostService)` and `AddService(Func<LogSpaceApplicationContext, IHostService>)`. The factory receives the same concrete context as Web's and Database's AddService, so hosting consumers can use environment, state, and hosted-service members beyond the small root contract. Database's root-level AddServer keeps the interface context. Factories run once per build against the same context retained by the application; the hosted-service snapshot is installed after factory evaluation. Services start in registration order and stop in reverse. No area-owned service abstraction is introduced.

## Dependency injection composition

`LogSpaceApplicationBuilder.Services` is the application's one composition registry, created with `EnableDynamicCode = false`, `ValidateOnBuild = true`, and `ValidateScopes = true`. The module registers only factories and instances, so the provider stays reflection-free. Every lifecycle service is an `IHostService` registration: `AddService(IHostService)` registers the caller's instance, `AddService(Func<LogSpaceApplicationContext, IHostService>)` registers a factory closed over the context, and a direct `Services.AddSingleton<IHostService>(...)` joins the lifecycle the same way. Order is registration order.

`Build` closes registration (the container becomes read-only, and a second `Build` throws), creates the provider once, and resolves `IEnumerable<IHostService>` once into the snapshot the host reuses for every start, stop, and rollback pass. The application owns the provider: disposal stops the host, then disposes the provider, which disposes every factory-created service in reverse creation order while instance registrations stay with their callers. A failed `Build` disposes the provider before rethrowing. The concrete context exposes the provider as `ServiceProvider` to hosting-layer factories; the area root and its features never see it.
