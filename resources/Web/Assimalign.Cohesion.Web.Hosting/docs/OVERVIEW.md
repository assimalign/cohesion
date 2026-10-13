# Assimalign.Cohesion.Web.Hosting

The Web runtime composes a concrete `WebApplication : Host<WebApplicationContext>`
through `WebApplication.CreateBuilder(args)`. It implements the root application and
builder contracts and integrates DI, configuration, logging, HTTP transports, and
the enabled resource's control plane at builder time.

## Composition and lifecycle

Feature packages register through `WebApplicationBuilder.Services`, with component-integration
verbs the application's compilation receives (`builder.Services.AddRouting()`, owner decision 34);
the root `IWebApplicationBuilder` members, `AddFeature` among them, are explicit shims over the same
registrations (`IHttpFeature`, `IWebApplicationServer`). Every `IHttpFeature` registration must be a
singleton typed as `IHttpFeature`, and no feature may be disposable: `Build()` rejects the rest,
naming the registration, except a disposable feature a factory produces, which the pipeline build
rejects when the factory first runs (decision 35). Background work is registered through the
concrete `WebApplicationBuilder.AddService` instance or context-factory overload, which
registers an `IHostService`. `Build()` closes registration and runs each service factory
once; services start in registration order before servers and stop in reverse order after
every server drains. The default server drains lame-duck style: it accepts nothing new, tells
every peer the connection is closing (`Connection: close` or `GOAWAY`), lets the requests in
flight finish within the host's shutdown budget, and cancels only what outlives it. Each exchange
sees the drain begin through `IWebServerDrainFeature` (a `Web.Server` contract), so a long-lived one, such as a WebSocket,
can end itself inside the budget. The internal `Assimalign.Cohesion.Web.Server` EventSource
reports lifecycle and failures, with `serverId` identifying each server in the process and no
request content in its payloads. See [Diagnostics](DESIGN.md#diagnostics). Disposing the application
disposes the service provider and every factory-created service.

The control-plane middleware registers its retained bootstrap verification handle directly with
WebApplicationContext's internal disposal owner (#1412, decision 57). Final application disposal
releases registered resources after the service provider, in reverse registration order;
a stop followed by restart keeps them alive.

Register middleware with `Use` before starting the application or explicitly building its
pipeline. A later `Use` throws `InvalidOperationException` because the pipeline is already built.

Every listener the default server composes gets five interceptors before any of the application's
own: the request limits, the HTTP/1.1 protocol upgrade, the HTTP/2 and HTTP/3 extended
CONNECT, the client fault, and response starting. The first surfaces the body-size cap and the minimum request-body and
response data rates per request (`context.MaxRequestBodySize`, `context.MinRequestBodyDataRate`,
`context.MinResponseDataRate`), so an endpoint whose body legitimately idles can disable the request
rate before it reads (#1383), and one whose reader pauses can disable the slow-read check before its
response starts (#1384). A client that grants no flow-control credit or stops reading is otherwise
reclaimed on every protocol, and its exchange is recorded as cancelled. The
next two make `context.Upgrade`, `context.ExtendedConnect` and a
WebSocket handshake (`context.WebSockets`) work on every protocol without listener configuration. A
request no handler accepts is served as before. The fourth publishes `IWebClientFaultFeature` on a
request that can have a body, on every version: when the body breaks its framing or a limit while it is
read, the transport answers `400`, `413`, `408` or `431` itself (on HTTP/2 and HTTP/3 it resets the
stream for a malformed body instead, reported as `400`), and the HTTP logging middleware and the
exception boundary treat the read's exception as the client's fault, not an application defect
(#1340, #1378). A `UseServer` callback that clears `options.Interceptors` removes them, and with them
WebSockets on every protocol (the HTTP/2 and HTTP/3 transports keep advertising extended CONNECT, but
nothing surfaces it) and the client-fault classification.

A handler that throws before its response starts is answered with a bare `500`. So is a response
the transport refuses to send because a header or trailer name is not a token or a value holds CR,
LF, NUL, or another control character but HTAB — typically request text copied into a header
unvalidated, which would otherwise split the response.

Cleartext HTTP/2 rejects `:scheme https` with `421` before dispatch. A trusted gateway that
forwards that scheme to an h2c backend can opt in with `options.AllowAlternateSchemes = true`
inside `Server.UseServer`, or with `Http:AllowAlternateSchemes` through `UseConfiguration`.
The opt-out preserves the gateway's scheme in `Request.Scheme`.

Every final response carries an automatic `Date` unless the application supplied one. To let a
proxy add its own, set `options.AddDateHeader = false` in `Server.UseServer`, or configure
`Http:AddDateHeader` through `UseConfiguration`. Disabling it suppresses only the automatic
header; StaticFiles retains `Last-Modified` and date validators. It uses a valid response `Date`,
or one clock read when that field is absent, invalid, or repeated, without adding or replacing
`Date`. Interim responses carry no `Date`.

TLS endpoints, including HTTP/3, default to a 10-second handshake timeout. Set
`Http:Endpoints:<name>:HandshakeTimeout` through `UseConfiguration` to a `TimeSpan` string or
whole seconds; `"infinite"`, `"-1"`, and non-positive durations disable it. The programmatic
HTTP/3 TLS overload uses the same setting as the TCP TLS listeners:

```csharp
builder.Server.UseServer(options => options.UseHttp3(
    quic => quic.EndPoint = new System.Net.IPEndPoint(System.Net.IPAddress.Any, 443),
    new TlsServerOptions
    {
        AuthenticationOptions = { ServerCertificate = certificate },
        HandshakeTimeout = TimeSpan.FromSeconds(5)
    }));
```

`TlsServerOptions` comes from `Assimalign.Cohesion.Connections.Security`. Its timeout overrides
the QUIC callback's value; any non-positive value disables it. With the raw QUIC overload, set
`quic.HandshakeTimeout` directly and use `Timeout.InfiniteTimeSpan` to disable it. Raw QUIC zero
is passed to the runtime and may expire immediately. Non-default TLS and configuration values
now affect HTTP/3 where 10.0.0-preview.1 ignored them; the default stays 10 seconds.

## Telemetry

The default server traces and measures every request (#1064). Subscribe by name:

- **Traces:** the `ActivitySource` `Assimalign.Cohesion.Web.Server` emits one `Server` span per
  request, parented to the caller's W3C `traceparent`, named `GET /orders/{id}` once routing has
  selected the endpoint, and tagged per the OpenTelemetry HTTP server conventions.
- **Metrics:** the `Meter` `Assimalign.Cohesion.Web.Server` emits `http.server.request.duration`
  (seconds) and `http.server.active_requests`.
- **Request id:** `context.Features.Get<IWebRequestIdFeature>()?.RequestId` is the request's trace id,
  with or without a listener.

A request's span carries its query as `url.query` with every value redacted
(`access_token=REDACTED&page=REDACTED`). Declare the parameters whose values are safe to show:

```csharp
builder.Server.AllowQueryValuesInTelemetry("page", "sort");
```

With no listener the server creates no activity and records nothing. Exporting these signals is not
this module's job; see [server telemetry](../../Assimalign.Cohesion.Web.Server/docs/DESIGN.md#server-telemetry-1064) for the attributes, the outcomes and
what is deliberately not emitted.

## Dependencies and hosting integrations

Within its area the module references the Web root, `Web.Routing`, `Web.Server`, and `Web.Health`.
Routing supplies the pipeline terminal; Server supplies the engine, telemetry and exchange features,
including request id, completion and drain; Health supplies the independent health model used by the
Hosting.Health adapter. COHRES002 permits Web libraries except Web.Testing, Web.ApplicationModel,
Web.ApplicationModel.Orchestration, the App.Web producers, and harnesses. The two ApplicationModel
packages also stay outside its resolved closure. Every reference ships in each area framework
that carries this module.

The module also references Cohesion hosting, configuration, DI, logging, and transport
infrastructure. It integrates Hosting.Health and Hosting.Resources itself (owner decision 57,
2026-10-13); the Web root references no hosting library. Public integration namespaces stay
`Assimalign.Cohesion.Web.Hosting.Health` and `.Resources`, but package references now use
`Assimalign.Cohesion.Web.Hosting`. The former two packages are removed.
All public composition is explicit and AOT-compatible. See [Design](DESIGN.md) for
listener ownership, cancellation, failure isolation, and control-plane behavior.

## Response-starting callbacks

Read `context.Features.Get<IWebResponseStartingFeature>()` and call `OnStarting(callback, state)`
to stage headers at the final commit point. Callbacks run once, in reverse registration order, for
buffered and streamed responses on every protocol. Do not write the response from a callback.
A callback failure aborts before the head is written. Callback header edits survive a refused head's
buffered replacement without callbacks running again. Pass optional `preservedHeaders` keys to also
retain policy fields accepted unchanged; their final values or absence are captured after callbacks.
The default interceptor supplies this feature;
clearing the interceptors removes it. SecurityHeaders and CORS require it, including on custom servers.

## Default EventSource forwarding

`WebApplication.CreateBuilder(args)` forwards `Assimalign.Cohesion.Web.` sources and
`Assimalign.Cohesion.Http.Connections` into the application's configured log providers. It adds no
provider or filter rule. Set `builder.EnableEventSourceForwarding = false` to opt out, or register
`builder.Logging.AddEventSourceForwarding(options)` before Build to choose your own sources. Any
application forwarder registration takes precedence. Explicit/options-based builders used for nested
Web hosts register no automatic forwarder.

Listeners observe the entire process. Concurrent entry-point applications can forward the same event;
use `serverId` payloads for server identity and `resourceName` for control-plane identity, and opt
out in secondary hosts that share sinks. Both use the Web.Hosting provider. Forwarding
ends when the application disposes its logger factory, after shutdown diagnostics. HTTP access logs
remain Information-level ILogger entries from Web.Diagnostics, independent of this forwarding.

Sdk.Web enables EventSource support for NativeAOT when unset. An explicit
`<EventSourceSupport>false</EventSourceSupport>` disables delivery. See the
[SDK design](../../../../sdks/Assimalign.Cohesion.Sdk.Web/Tasks/docs/DESIGN.md) for size measurements.

Compared with 10.0.0-preview.1, entry-point applications now forward these framework events into
their existing sinks. The built application owns its logger factory and disposes its providers and
forwarders at final teardown; a factory obtained from its service provider cannot outlive the app.

## Health integration

The adapter in Web.Hosting extends `IHealthChecksBuilder` with `AddContributor`.
It references `Assimalign.Cohesion.Web.Health` and `Assimalign.Cohesion.Hosting.Health`.
The Web health model and HTTP endpoints remain in `Web.Health`.

### Usage

```csharp
using Assimalign.Cohesion.Web.Health;
using Assimalign.Cohesion.Web.Hosting.Health;

IHealthCheckService health = HealthChecks.CreateBuilder()
    .AddContributor(contributor)
    .Build();
pipeline.MapHealthChecks(health);
```

The contributor name identifies the check. Omitted tags include readiness and liveness;
explicit tags replace those defaults. Failure status and timeout use Web health policy.
The application owns contributor lifetime; the adapter only evaluates it.

`App.Web` exposes the adapter through Web.Hosting. Database.Hosting consumes the module through
a private project reference paired with Web.Hosting and Web.Health in the App.Database runtime
pack. See [Health integration](DESIGN.md#health-integration) for dependency and cancellation contracts.

## Resource control plane

Install the resource protocol on a private Web application's pipeline:

```csharp
using Assimalign.Cohesion.Web.Hosting.Resources;

IWebApplicationPipelineBuilder pipeline = privateApplication;
pipeline.UseResourceControlPlane(controlPlane, resourceContext,
    () => resourceApplication.Context.State is HostState.Started);
```

Install before application middleware. An optional trailing port gates every route; null serves every listener. Hosts composing manually retain `ResourceControlPlaneMiddleware.CreateLifetime(resourceContext)` while serving requests through `InvokeAsync(...)`, then dispose the lease after those requests drain. Stop uses Web.Server's response-completion feature to send its acknowledgement before requesting shutdown. The feature serves health, readiness, liveness, endpoint discovery, stop, and command envelopes. Gateway-managed namespaced routes authenticate against the published application trust key; standalone namespaced routes accept only transport peers at `127.0.0.1`, `::1`, or IPv4-mapped `127.0.0.1` and return 403 to other or unknown peers. Forwarded headers do not grant access. Bare `/healthz`, `/readyz`, and `/livez` stay public. Remote standalone management is no longer supported (a behavioral break from 10.0.0-preview.1, #1431). Domain command handlers remain the owning area's responsibility.

Managed middleware reuses one ECDSA verification handle while its lifetime leases are active
(#1412). `UseResourceControlPlane` transfers its lease directly to WebApplicationContext's
internal disposal registration and releases it during final application disposal. A stop and
restart keep it alive. A custom managed pipeline context outside Web.Hosting fails composition
with a clear `InvalidOperationException`, a behavior change from 10.0.0-preview.1; use the manual
lifetime API below. A disposed composed pipeline refuses
further invocation.

This integration lives in Web.Hosting under `src/Resources/`, keeping the
`Assimalign.Cohesion.Web.Hosting.Resources` namespace. Its dependencies are Web root, Web.Server
(for `IWebResponseCompletionFeature`, #1379), Hosting.Resources, Hosting.Health, and
IdentityModel.Token.JsonWebToken. Web roots and features cannot reference the hosting module;
other areas' hosting modules consume it privately. App.Web exposes it publicly.

See [Resource control plane](DESIGN.md#resource-control-plane) for protocol, authentication, ownership, and parity constraints.

### Manual composition API

`ResourceControlPlaneMiddleware.Validate(ResourceContext resourceContext)` performs the verb's eager managed-identity validation. It rejects a null context, a blank managed resource name, a missing application identity or public trust key, and an invalid P-256 JWK. Standalone contexts need no identity.

`ResourceControlPlaneMiddleware.CreateLifetime(ResourceContext resourceContext)` performs the same
validation and returns an idempotent `IDisposable` lease. The first lease imports the verification
handle; the last disposal releases it. Multiple owners of the same resource context share that
handle, and verification is synchronized with disposal. Keep the lease across the manual host's
request loop:

```csharp
using IDisposable lifetime =
    ResourceControlPlaneMiddleware.CreateLifetime(resourceContext);

// exchanges is the host's request stream; end it only after accepting has stopped.
await foreach (IHttpContext exchange in exchanges)
{
    await ResourceControlPlaneMiddleware.InvokeAsync(
        controlPlane, resourceContext, isApplicationReady(), controlPlanePort, exchange, next);
}
// Every request above has completed before the lifetime scope ends.
```

`ResourceControlPlaneMiddleware.InvokeAsync(IResourceControlPlane controlPlane, ResourceContext resourceContext, bool isApplicationReady, int? controlPlanePort, IHttpContext context, WebApplicationMiddleware next)` returns a `Task` for one exchange. Required reference arguments cannot be null. A mismatched non-null port forwards every path to `next`; null applies no port gate. Unknown namespaced paths apply the gateway credential check or standalone loopback restriction before returning 404.

Acquire the lifetime during composition, then register `InvokeAsync` ahead of routes the terminal should own. The owning host supplies readiness and listener selection and drains concurrent requests before disposing the lease. Existing callers that only call `Validate` and `InvokeAsync` remain supported: they reuse the parsed key and validation profile but create and dispose a temporary ECDSA handle for each signed token. Stop registers a callback on `IWebResponseCompletionFeature` when present; the callback requests stop with `CancellationToken.None` after transmission. A custom server without the feature uses the request token for the direct-stop fallback.

The server accepts HTTP connections through `AcceptAsync` and reports `ServerBound` addresses
from `IHttpConnectionListener.EndPoints` after binding. Raw `UseServer` listener registrations
report the same assigned TCP or QUIC ports as the hosting convenience registrations.
