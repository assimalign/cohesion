# Assimalign.Cohesion.Web.Server — Overview

The Web server engine and the contracts it publishes on each exchange. Construct
`WebApplicationServer` with a pipeline and listener options to serve requests without the hosting
module. The server installs its own client-fault and response-starting interceptors; middleware
and handlers read its exchange features from `context.Features`. Web.Hosting composes the same
public engine behind a non-owning `WebApplicationServerHostService`.

## Scope

- **Request id** — `IWebRequestIdFeature.RequestId` is the request's W3C trace id: the server
  span's trace id when the server traces the request, otherwise the trace id of a valid
  `traceparent`, otherwise a random id generated on first read (#1064).
- **Server activity** — `IWebRequestActivityFeature.Activity` is this exchange's server span,
  or `null` when no span was created. It is independent of `Activity.Current`, which may be a
  child or unrelated ambient activity. The server owns the activity's lifetime (#1297).
- **Response starting** — `IWebResponseStartingFeature.OnStarting(callback, state)` queues work
  before the final head. It runs once in reverse order; a failure aborts before serialization.
  Callback header changes survive a refused head's buffered replacement without rerunning callbacks.
  Optional `preservedHeaders` keys also retain policy fields accepted unchanged by the callbacks.
- **Response completion** — `IWebResponseCompletionFeature.Register` queues callbacks that run in
  order after the response is written to the transport. Registration after completion throws.
  `Web.Hosting` defers a control-plane stop until its `202` is written this way.
- **Drain signal** — `IWebServerDrainFeature.Draining` is cancelled when the server begins its
  lame-duck drain, so a long-lived exchange can end itself within the stop's budget. It cancels
  nothing. `Web.WebSockets` closes open sockets with `1001 Going Away` on it.
- **Client fault** — `IWebClientFaultFeature.StatusCode` is the `4xx` the transport answers the
  exchange with because the request body broke its framing or a configured limit while it was read
  (`400`, `413`, `408` or `431`), or `null`. A connection lost mid-body instead fires `RequestCancelled`
  and throws `OperationCanceledException` carrying that token, with no client-fault status (#1439). On
  HTTP/2 and HTTP/3 a malformed body resets the stream instead and is reported as `400`. The read still
  throws; this tells the code that sees the exception it was the client's fault, not an application
  defect (#1340, #1378). The default server installs it on a request that can have a body, on every
  version. `Web.Diagnostics`, `Web.ErrorHandling` and
  `Web.Compression` read it.

- **Response client fault** — `IWebResponseClientFaultFeature.ResponseAbortedByClient` reports that
  the peer abandoned the response or could not receive it at the configured minimum rate. The
  default server publishes it even for bodyless requests. `Web.ErrorHandling` uses this report to
  cancel without fault observation, including when middleware wraps the transport exception (#1432).

A custom `IWebApplicationServer` may omit any of the seven, so every reader handles an absent
feature. SecurityHeaders and CORS require starting callbacks and throw if the feature is absent.

## Namespace

The public server, its options and the contracts declare `Assimalign.Cohesion.Web`. The contracts
keep the namespace they used in the Web root; their earlier move here (decision 33, #1379) changed
assembly, not namespace. The newly public server and options use the package's pinned root namespace
(decision 57, #1458).

## Dependencies

`Assimalign.Cohesion.Web`, `Assimalign.Cohesion.Http`, `Http.Connections`, `Connections` and
`Web.Routing` (the endpoint route template for `http.route`). The engine has no Hosting, DI,
configuration, connection-driver or HTTP default-interceptor package reference.

Web.Routing cannot reference Web.Server because the reverse edge would create a dependency cycle.

## Public options

- `Pipeline` supplies the required `IWebApplicationPipeline`.
- `Listener` is a `HttpConnectionListenerOptions` initialized to a new instance. Register the
  transport listeners there; the server constructs its HTTP listener from these options.
- `MaxConcurrentConnections` is an optional positive cap; null is unlimited.
- `TelemetryQueryValues` is a case-insensitive set of query names whose values may appear in
  request spans. The server snapshots it at construction; all other query values remain redacted.

The constructor validates the options, adds its own exchange-feature capacity and rounds once.
A direct application uses the same request-id, response-starting, client-fault and drain behavior
as the hosted engine. Add desired HTTP feature interceptors to `Listener.Interceptors` explicitly;
Web.Hosting supplies request limits, protocol upgrade and extended CONNECT for its hosted path.

## Direct use

This routine takes a pipeline and an application-selected duplex transport listener. The application
references whichever transport driver it chooses; Web.Server itself has no driver dependency.

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Http.Connections;
using Assimalign.Cohesion.Web;

static async Task ServeAsync(
    IWebApplicationPipeline pipeline,
    IDuplexConnectionListener transport,
    CancellationToken stopping)
{
    var listener = new HttpConnectionListenerOptions();
    listener.UseHttp1(transport);
    var server = new WebApplicationServer(new WebApplicationServerOptions
    {
        Pipeline = pipeline,
        Listener = listener,
        MaxConcurrentConnections = 256,
    });

    try
    {
        await server.StartAsync(stopping);
        await Task.Delay(Timeout.InfiniteTimeSpan, stopping);
    }
    catch (OperationCanceledException) when (stopping.IsCancellationRequested)
    {
    }
    finally
    {
        using var drain = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await server.StopAsync(drain.Token);
    }
}
```

`StartAsync` completes after every endpoint binds and propagates the original transport failure.
`StopAsync` stops acceptance, drains in-flight work within its cancellation budget and releases the
listener. The server is not an `IHostService`; Web.Hosting provides that integration separately.

## Diagnostics

The EventSource, Meter and ActivitySource all use `Assimalign.Cohesion.Web.Server`. The source
reports bind, connection and drain lifecycle/failures, refused heads and callback/telemetry failures;
its counters are `connection-faults`, `connection-cap-waits` and `refused-response-heads`.
The Meter publishes `http.server.request.duration` and `http.server.active_requests`, and the
ActivitySource publishes one server span per dispatched request. Direct applications choose their
listeners or forwarding; the entry-point host's default `Assimalign.Cohesion.Web.` prefix covers
both Web.Server and Web.Hosting. See [DESIGN.md](DESIGN.md#diagnostics) for payloads and throttling.

Making the engine public and relaxing hosted `UseServer<TServer>` to accept any
`IWebApplicationServer` are additive changes. The three diagnostic names move from Web.Hosting;
none shipped in 10.0.0-preview.1 (decision 15).

## Exchange feature usage

```csharp
// The 32-character lower-case trace id, or null under a server that does not publish it.
string? requestId = context.Features.Get<IWebRequestIdFeature>()?.RequestId.ToHexString();

// This exchange's server activity, or null when it has no span. Do not stop or dispose it.
Activity? serverActivity = context.Features.Get<IWebRequestActivityFeature>()?.Activity;

// Stage a final header. Callbacks must not start or write the response themselves.
context.Features.Get<IWebResponseStartingFeature>()?.OnStarting(state =>
{
    ((IHttpContext)state!).Response.Headers[new HttpHeaderKey("x-example")] = "value";
    return Task.CompletedTask;
}, context);

// Defer work until the response has reached the transport.
context.Features.Get<IWebResponseCompletionFeature>()?.Register(() => ValueTask.CompletedTask);

// End a long-lived exchange cleanly when the server starts draining.
CancellationToken draining = context.Features.Get<IWebServerDrainFeature>()?.Draining ?? CancellationToken.None;

// After a body read threw: was it the client's fault, and which status does the transport send?
HttpStatusCode? clientFault = context.Features.Get<IWebClientFaultFeature>()?.StatusCode;

// After a response write failed: did the transport end it because of the client?
bool responseAbandoned = context.Features.Get<IWebResponseClientFaultFeature>()?.ResponseAbortedByClient == true;
```

`Sdk.Web` applications get the package through the `App.Web` shared framework. A library that reads
one of these features references the package directly. Design detail: [DESIGN.md](DESIGN.md).
