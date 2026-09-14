# LogSpace Hosting design

## Platform sink and boundaries

Item 31b makes LogSpace the Platform sink for Hosting.Telemetry. The resource root remains contract-only; all composition is in Hosting. OTLP/HTTP JSON logs are accepted, protobuf receives 415 with an OTLP/JSON-only detail. /v1/traces and /v1/metrics are reserved POST routes returning 501 after authentication/content validation: libraries/Logging has no span or instrument primitive, and protobuf is deferred. No gRPC framing or response-trailer support exists in the HTTP server; therefore the SDK otlp endpoint changed from grpc/tcp 4317 to https/tcp 4318, Certificate=tls. query stays https/tcp 8443. Both Public flags remain false: application reachability through observed endpoints is not external exposure.

## HTTP and TLS

`Local` is the developer-machine environment. `Development` is a deployable environment and requires a mounted certificate even on loopback, as does `Production`. An unset environment still resolves to `Production`.

OtlpReceiverEndpointService mirrors LogSpaceControlPlaneEndpointService: parameterless WebApplication.CreateBuilder avoids recursive ambient registration, TryGetEndpointCertificate(endpoint, out leaf, out chain) builds the TLS certificate context, and only loopback Local permits CreateDevelopmentEndpointCertificate fallback. Both listeners fail closed otherwise. One tls Secret mount may back both listeners. Outbound exporters use CreateOutboundTrustValidator and the application trust bundle; there is no blanket certificate bypass.

Middleware checks known path, method, authentication and application/json content. Wrong method yields 405/Allow: POST, unknown path 404, malformed JSON 400, missing credential 401/Bearer challenge, wrong audience or scope 403, oversized body 413 and unavailable storage 503. Http.RequestLimits is a private reference. LogSpace owns the one-MiB Content-Length check and bounded streaming read, returning 413 with Connection: close. The listener body cap is disabled because its post-dispatch Http1LimitExceededException currently closes the connection without a 413; header and data-rate transport limits remain in force. No HTTP-area code was changed. JSON uses a depth bound and at most 8192 records per request. Full acceptance returns {"partialSuccess":{}}; queue overflow returns rejectedLogRecords as an int64 string. Traces/metrics have no acceptance path.

## Authentication and middleware order

The private ES256 verifier is modelled on Web.ControlPlane.BootstrapTokenVerifier: issuer=application, key ID/algorithm/signature, required iss/sub/aud/exp/nbf/iat/jti, at most 24-hour lifetime, audience=LogSpace resource name. Ingest additionally requires scope=telemetry and a nonblank subject identifying the emitter; service.name must equal that subject. The gateway uses a distinct (application,sink,emitter) token cache; tokens are not the sink's bootstrap credential.

The query middleware is registered BEFORE UseResourceControlPlane in LogSpaceControlPlaneEndpointService. ResourceControlPlaneMiddleware.InvokeAsync authenticates unknown /cohesion/v1/* paths then returns 404 (Web.ControlPlane/src/Internal/ResourceControlPlaneMiddleware.cs:44-59,185-191); it would swallow a later query handler. The earlier LogSpace middleware rejects telemetry-scoped credentials on every namespaced query/management request before forwarding other routes, protecting /stop and /commands despite the unchanged shared verifier. Query accepts only own-name audience with gateway subject and ordinary bootstrap/dev tokens. No cohesion-export audience was added.

## Storage, lifecycle and query

ResourceContext.GetMount("data", ContentRootPath/logs) selects the filesystem store. Receiver threads enqueue immutable records to a channel bounded to 8192 entries and a conservative 64 MiB estimated record budget. SegmentFlushService owns synchronous writes and fsync on its dedicated thread, every 250 ms and on stop; receivers stop before the flush service. Files are append-only logs-YYYYMMDD-NNNN.ndjson, rotated daily or at 16 MiB; each restart opens a fresh lexically ordered segment. Records have t (nanosecond string), sev, sevText, svc, cat, body and attrs. A per-segment .index records first/last timestamps and count; the minimal query currently scans segments instead of relying on this sidecar. IOException/UnauthorizedAccess marks the store unavailable, retaining queued entries for a subsequent flush and yielding 503 rather than stopping the host. Retention and crash-tail repair remain future work.

Queries filter service.name and timestamp, cap limit to 1000, and return bounded NDJSON. Base64 cursors carry segment/line position and a filter fingerprint, with path validation; they grant no authorization. A cursor on the final full page may yield an empty next page. Producer-controlled JSON uses manual JsonDocument validation; stored records and cursors use source-generated LogSpaceJsonContext, without reflection serialization.

R-5 explicitly defers the developer-experience design §8 Database.Embedded storage requirement to the Database MVP embedded-consumption phase; this segment implementation is not a replacement architectural decision. No cross-area Database reference was added.

## Telemetry ordering

The gateway injects only after a same-application LogSpace is Running with an observed otlp endpoint; no self-export is injected. A producer prepared earlier gets no telemetry; a later preparation can receive it, while an already-running process needs restart to read changed environment. No implicit DependsOn was added. Explicit producer.DependsOn(sink) establishes deterministic startup. RemoteReference injection awaits an authenticated named-OTLP resolution contract.

## Non-goals

LogSpace.Telemetry remains empty project scaffolding, distinct from Hosting.Telemetry. Verifier consolidation, inferred telemetry dependencies, retention/archival, protobuf, gRPC, traces and metrics are separate deliverables.
