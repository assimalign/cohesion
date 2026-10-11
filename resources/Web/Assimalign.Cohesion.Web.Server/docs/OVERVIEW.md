# Assimalign.Cohesion.Web.Server — Overview

The contracts the Web server publishes on each exchange. Contracts only: the default server in
`Assimalign.Cohesion.Web.Hosting` installs them before the pipeline runs, and middleware and
handlers read them from `context.Features`.

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
  `Web.Hosting.Resources` defers a control-plane stop until its `202` is written this way.
- **Drain signal** — `IWebServerDrainFeature.Draining` is cancelled when the server begins its
  lame-duck drain, so a long-lived exchange can end itself within the stop's budget. It cancels
  nothing. `Web.WebSockets` closes open sockets with `1001 Going Away` on it.
- **Client fault** — `IWebClientFaultFeature.StatusCode` is the `4xx` the transport answers the
  exchange with because the request body broke its framing or a configured limit while it was read,
  or the client cut it short by closing the connection (`400`, `413`, `408` or `431`), or `null`. On
  HTTP/2 and HTTP/3 a malformed body resets the stream instead and is reported as `400`. The read still
  throws; this tells the code that sees the exception it was the client's fault, not an application
  defect (#1340, #1378). The default server installs it on a request that can have a body, on every
  version. `Web.Diagnostics`, `Web.ErrorHandling` and
  `Web.Compression` read it.

A custom `IWebApplicationServer` may omit any of the six, so every reader handles an absent
feature. SecurityHeaders and CORS require starting callbacks and throw if the feature is absent.

## Namespace

The contracts declare `Assimalign.Cohesion.Web`, the namespace they shipped in when the Web root
held them. Moving them here (owner decision 33, #1379) changed their assembly, not their name, so
call sites compile unchanged.

## Dependencies

`Assimalign.Cohesion.Web` (the root, for `IWebApplicationServer` in the contracts' documentation)
and `Assimalign.Cohesion.Http` (`IHttpFeature`). No Hosting, DI or configuration reference.

## Usage

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
```

`Sdk.Web` applications get the package through the `App.Web` shared framework. A library that reads
one of these features references the package directly. Design detail: [DESIGN.md](DESIGN.md).
