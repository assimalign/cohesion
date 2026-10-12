# Web area design

## Control-plane integration

Web.Hosting.Resources is a hosting-family integration over the root pipeline seam. It references the Web root, Web.Server, Hosting.Resources, Hosting.Health, and IdentityModel.Token.JsonWebToken, and never Web.Hosting. COHRES001 prevents roots and features from consuming this integration; O35 lets the exact Web.Hosting module consume its own hosting family under COHRES002. Web.Hosting and Database.Hosting now use this single terminal, with Web.Server's IWebResponseCompletionFeature seam for deferred stop. The seam lived in the Web root until #1379 moved the server's per-exchange contracts to Web.Server (owner decision 33). A real Program factory compares the Web wrapper with the direct verb as a composition regression. Web.Testing names both runtime and terminal in its explicit exemption.

The integration is public in App.Web and private in each generic resource framework. Area ApplicationModel packages remain NuGet-only. Cross-area hosting implementation references use the coordinated CohesionPrivateProjectReference / CohesionFrameworkPrivateAssembly pair. The integration owns authentication and AOT-safe JSON; each area host owns its listener, readiness gate, and lifecycle.

Web.Hosting.Health integrates Hosting.Health contributors with the independent Web.Health model.
Database.Hosting consumes it privately for admin health checks, retaining its readiness-only accepting gate.

See [the package design](../../../resources/Web/Assimalign.Cohesion.Web.Hosting.Resources/docs/DESIGN.md) for the exact protocol and the public response-completion contract.

Both runtimes consume the control-plane integration, and the integration depends on the Web root and Web.Server without a reverse runtime reference.

```mermaid
flowchart LR
    WebHost["Web.Hosting"] --> Terminal["Web.Hosting.Resources"]
    DbHost["Database.Hosting"] --> Terminal
    Terminal --> Web["Web"]
    Terminal --> Server["Web.Server"]
```

## Diagnostic signal ownership

Web and transport diagnostics answer different questions. Web.Hosting owns the server spans and
Meter instruments for requests that reach the Web pipeline. The HTTP transport also rejects input before
dispatch and enforces protocol rules after dispatch; it owns those outcomes even when no Web span
exists. Each owner reports its own events rather than forwarding another package's internal source.

| Question or outcome | Signal | Owner | What it reports |
| --- | --- | --- | --- |
| What happened during a dispatched HTTP request? | `ActivitySource` named `Assimalign.Cohesion.Web.Hosting` | Web.Hosting | One server span, caller trace context, method, route, final status, duration and cancellation/fault outcome. |
| How much dispatched work is active, and how long does it take? | `Meter` named `Assimalign.Cohesion.Web.Hosting` | Web.Hosting | `http.server.active_requests` and `http.server.request.duration`, including Web middleware's rejection statuses. |
| Did HTTP reject a request or fail a protocol operation? | `Meter` and `EventSource` named `Assimalign.Cohesion.Http.Connections` (#1300) | HTTP transport | Pre-dispatch rejection statuses, HTTP/2 and HTTP/3 protocol errors, request-rate enforcement and slow-reader response aborts. These are not Web application faults. |
| Did a listener bind, drain or stop, and did server machinery fail? | `EventSource` named `Assimalign.Cohesion.Web.Hosting` | Web.Hosting | Lifecycle, bind/accept/connection failures, completion-callback failures, refused response heads, capacity waits and drain timeouts, correlated by process-unique `serverId`. Peer-ended connections are Verbose and do not increment `connection-faults`. |
| Which application fault reached the exception boundary? | `EventSource` named `Assimalign.Cohesion.Web.ErrorHandling` | Web exception boundary | Pipeline and handler failures, expected faults and abandoned responses; client-caused and cancelled exchanges do not become application-fault counts. |
| Which middleware rejected or degraded an exchange, and why? | The owning Web assembly's `EventSource` | Web feature package | Bounded rejection reasons and exact attribution counters for authentication, authorization, antiforgery, edge policies, timeouts, rate limits, compression, forms and content handling. There are no duplicate request start/stop events or general request counters. |
| Did health or control-plane work fail? | `Web.Health`, `Web.Hosting.Resources` and `Hosting.Resources` event sources | Web health/HTTP control surface, or shared control-plane producer | Health transitions and failed checks; HTTP authentication/command-input failures; producer-owned contributor failures and command outcomes. |
| Did the underlying connection or TLS handshake fail? | Connections driver event sources; runtime `System.Net.Security` | Connection transport and TLS runtime | Listener, connection and stream lifecycle, driver counters and TLS handshake diagnostics. These sources are outside default Web forwarding. |
| What should the application's HTTP access log contain? | `ILogger` entries, default category `Assimalign.Cohesion.Web.Diagnostics.HttpLogging` | Web.Diagnostics middleware | Explicitly composed access logging at Information with its own redaction options. The `Web.Diagnostics` EventSource reports failures of logging itself, rather than duplicating access entries. |

The [event-source catalog](../../EVENT_SOURCES.md#cohesion-event-sources) lists all 22 instrumented Web
assemblies and links their event schemas, counters and payload policies. The
[deliberately uninstrumented list](../../EVENT_SOURCES.md#web-assemblies-deliberately-left-uninstrumented)
records where visible status codes, thrown failures or contract-only packages need no extra source.
The package-level details of request spans and metrics live in
[Web.Hosting's telemetry design](../../../resources/Web/Assimalign.Cohesion.Web.Hosting/docs/DESIGN.md#server-telemetry-1064),
and its operator events in
[Web.Hosting's diagnostics design](../../../resources/Web/Assimalign.Cohesion.Web.Hosting/docs/DESIGN.md#diagnostics-147-1398).

Request event correlation and the access log read `IWebRequestIdFeature`, published by Web.Hosting
through the Web.Server contract. Its identifier remains stable when no `ActivityListener` is
attached; sources never infer it from ambient `Activity.Current`. Event payloads omit request paths,
queries, header values and bodies. A `routeTemplate`, when the owning package references routing,
describes the configured route rather than the request target. Application exception messages remain
subject to each package's documented payload policy.

Entry-point `CreateBuilder(args)` applications forward the `Assimalign.Cohesion.Web.` prefix and
the exact `Assimalign.Cohesion.Http.Connections` name into configured logging sinks. Nested builders
register no default listener, and an application-selected forwarder or
`EnableEventSourceForwarding = false` takes precedence. Forwarding changes neither providers nor
filter rules, so the HTTP access log's Information level is preserved. Event listeners observe the
whole process: `serverId` and request identifiers provide attribution when several servers coexist.
EventSource counters are inspected separately from forwarded events, and Web feature Meter
instruments remain the distinct #317 workstream.
