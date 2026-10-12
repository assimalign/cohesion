# Web.Hosting.Resources design

The hosting-family integration extends `IWebApplicationPipelineBuilder` with `UseResourceControlPlane(controlPlane, resourceContext, isApplicationReady, controlPlanePort = null)`. Callers install it first on a private listener. It owns no port, host, service container, or configuration provider. The readiness callback observes the owning resource host's `HostState.Started`, not merely listener startup.

The route and envelope contract matches Web.Hosting's terminal: public `/healthz`, `/readyz`, `/livez` aliases; namespaced health, readiness, liveness, endpoints, stop, and commands beneath `/cohesion/v1`. Reads allow GET/HEAD, stop allows POST, and commands allow GET/HEAD/POST/DELETE. Unknown namespaced routes are 404 after the access check. Health reports preserve diagnostic values and ordinal contribution/endpoint ordering. HEAD suppresses bodies. The bare probes are never authorized, so the health writer publishes only what the control plane reports: a contributor that throws appears with the fixed description `The contributor threw an exception.`, never its exception message, and an exception a contributor places in its data appears as `The contributor reported an exception.`, never its `ToString()` (Hosting.Resources design, #1409). The writer's own data switch writes the same fixed text for an `Exception` value, as defense in depth: a custom `IResourceControlPlane` (`ResourceRuntime.RegisterControlPlane` accepts any implementation) passes its report through unfiltered, and every shipped area's control plane is the default one. Other unrecognized values are still written through `ToString()`.

Managed namespaced requests first consult the resource's registered `IResourceCredentialVerifier` (`ResourceRuntime.TryGetCredentialVerifier(resourceContext)`, see the Hosting.Resources design), passing the `Authorization` value split into scheme and credential. A `NoResult` verdict falls through to the default application-key verification; any other verdict is final. The default verifies an ES256 JWT with the published application P-256 JWK: issuer/application, subject/gateway, key id, signature, required temporal claims and jti, and at most 24 hours from issuance to expiry. It is a thin configuration of the shared `JsonWebTokenValidator` (IdentityModel.Token.JsonWebToken) with `ResourceCredentialProfile` constants (Hosting.Resources); the JWK parsing, signature, and claim rules are no longer copied here. Either path yields a `ResourceCaller`, and the management plane admits only `Kind=Gateway` callers whose `Application` is the ambient application and whose `Subject` is the ambient gateway — for application-key tokens exactly the former `iss`/`sub` rule. Invalid credentials are 401 with a Bearer challenge; an authentic credential for another resource audience, or a caller that is not this application's gateway, is 403. The internal verifier, parsed application JWK and ES256 validation profile are cached once per `ResourceContext` in a middleware-owned `ConditionalWeakTable` of `Lazy<BootstrapTokenVerifier>`. Composition validation and requests use the same entry; concurrent first requests initialize it once, and separate resource contexts never share trust. Weak keys allow completed in-process invocations to be collected. Tokens rotated at reconciliation are accepted without byte-equality pinning. The shared IdentityModel validator still creates and disposes an ECDSA handle for each signature validation: its public non-disposable contract offers no retained-handle mode, and the static middleware/pipeline contract offers no lifetime-disposal hook. Moving that handle to middleware lifetime requires a separate ownership seam; this change caches the parsed key and validator without widening either boundary (#1412).

Without a gateway identity, standalone execution is for development: `/cohesion/v1` and every path beneath it admit only transport peers at `127.0.0.1`, `::1`, or IPv4-mapped `127.0.0.1`. The middleware reads `IHttpContext.ConnectionInfo.RemoteEndPoint` as an `IPEndPoint`, normalizes IPv4-mapped addresses, and refuses every other address, including absent and non-IP endpoints. Forwarded headers and effective forwarded-address features are not used. A refusal returns **403**, chosen to make the access restriction clear; it happens before route/method selection, body parsing, command dispatch, or stop registration. Unknown namespaced routes therefore return 403 to a refused standalone peer and 404 to an admitted one. The listener-port gate still applies first, and unrelated application routes are unchanged.

Bare `/healthz`, `/readyz`, and `/livez` probes stay open for orchestrators and load balancers. Their namespaced aliases have the same protection as other management routes. Gateway-managed requests retain the existing credential checks on every peer, including loopback. This is a behavioral breaking change from 10.0.0-preview.1 (#1431, owner decision 46, accepted on decision 15's preview terms): unauthenticated remote standalone management previously worked. Use a loopback connection for standalone development or a gateway identity and credentials for remote management.

Command discovery includes both acceptedCommandKinds and applied commands. Non-object envelopes, non-string/base64 payloads, and blank identity fields are 400. Unsupported kinds return 501 and ownership/replay rejections return 409, both with status=Rejected and detail. This package declares no command kinds or handlers.

O35 permits the exact Web.Hosting module to consume its own hosting family under COHRES002. COHRES001 still prevents roots and features from referencing it and prevents it from referencing Web.Hosting. Web.Hosting and Database.Hosting consume this single terminal. Web.Server's IWebResponseCompletionFeature defers stop until the transport has written 202; custom servers without the feature retain direct stop. The contract lived in the Web root until #1379 moved the server's per-exchange contracts to Web.Server (owner decision 33); its namespace stayed Assimalign.Cohesion.Web, so this package added a Web.Server reference and no code change. The executable regression compares the Web.Hosting wrapper through FromProgram with the direct verb, guarding both composition paths.

ResourceControlPlaneMiddleware exposes InvokeAsync for manually composed hosts and Validate for eager identity validation. UseResourceControlPlane calls Validate before registration. Its optional trailing controlPlanePort gates every route, including bare probes; null means no gate. Web.Hosting separately treats an unknown observed http/https port as disabled, so it forwards without invoking this terminal. Web and Database validate managed identity at Build only when their control-plane listener is installed.

**Breaking behavior change from 10.0.0-preview.1 (#1410).** The default Web server runs every response-completion callback in registration order even when an earlier callback throws, and reports each failure through its existing server logger. A middleware callback registered before the control plane can therefore no longer prevent the deferred stop after its `202` acknowledgement. The real-server regression authenticates as a gateway over the in-memory transport, whose non-IP peer is deliberately refused in standalone mode. It holds an earlier callback until the client receives `202`, lets it throw, and observes the control plane's stop request with `CancellationToken.None`.

Serialization uses Utf8JsonWriter and JsonDocument only. Dependencies are Web root, Web.Server, Hosting.Resources, Hosting.Health, and IdentityModel.Token.JsonWebToken. App.Web exposes the feature publicly; other areas consume its implementation privately. No ApplicationModel package enters a framework.

Tests may reference Web.Hosting and the sample Program: COHRES001/002 skip the tests leaf via `_CohesionHostingRuleApplies`; COHAM001/COHRES003 skip harness path segments via `_CohesionResourceBoundaryRulesApply` (Build.Rules.targets). These are separate gates, not exemptions.

The certificate contract is consumed by each owning host when it constructs an HTTPS listener. This middleware owns no TLS parser or listener and retains identical command and health behavior over either transport.

Web and Database compose the same terminal through its root contracts; the integration never references either runtime.

```mermaid
flowchart LR
    WebHost["Web.Hosting"] --> Terminal["Web.Hosting.Resources"]
    DbHost["Database.Hosting"] --> Terminal
    Terminal --> Web["Web"]
    Terminal --> Server["Web.Server"]
    Terminal --> Resources["Hosting.Resources"]
    Terminal --> Health["Hosting.Health"]
    Terminal --> JWT["IdentityModel.Token.JsonWebToken"]
```

## Diagnostics

The internal `ResourceControlPlaneEventSource` is named `Assimalign.Cohesion.Web.Hosting.Resources`.
It reports every area's control plane that composes this middleware, identified by `resourceName`:
Web and the nested HTTP hosts in ApiManager, Database, EmailHub, EventHub, IoTHub, LoadBalancer,
LogSpace, MediaHub, MessageHub, NatGateway, NotificationHub, Rezolvr and VpnGateway. Non-Web
resource processes observe it through diagnostic tools or their own EventSource forwarder; nested
hosts do not receive entry-point Web default forwarding. Area-specific endpoints that bypass the
shared middleware are outside this source.

| Id | Event | Level | Payload |
| --- | --- | --- | --- |
| 1 | CredentialRejected | Warning | resourceName, requestId, status, reason, route, remoteEndPoint, suppressedCount |
| 2 | CommandMalformed | Warning | resourceName, requestId, httpMethod, phase, exceptionType, suppressedCount |
| 3 | StopAccepted | Informational | resourceName, requestId, deferred |

Credential rejection includes the standalone non-loopback refusal introduced by #1431, with
`reason=non-loopback-peer` and `status=Forbidden`. Other reasons are `missing-credential`,
`unsupported-scheme`, `invalid-token`, `audience-mismatch`, `caller-mismatch` and `verifier-rejected`.
A registered verifier's private reason is never copied. `route` is a fixed category (stop, commands,
endpoints, healthz, readyz, livez or unknown), never a path. The transport peer appears only in
CredentialRejected and is personal data in the diagnostic sink; forwarded client addresses are not
used. Request identifiers come from `IWebRequestIdFeature`, never an ambient Activity.

CredentialRejected and CommandMalformed each emit at most once per second across the provider.
Their next emission includes the number of suppressed enabled-listener occurrences (saturating at
`Int32.MaxValue`). The `rejected-credentials` PollingCounter remains exact, including refusals when
disabled or throttled. It is created on first enable, and its backing total uses Interlocked.
No request-total counter or Meter instrument is added. StopAccepted is the scope's unthrottled
management lifecycle acknowledgement, with `deferred` indicating response-completion scheduling.

Malformed JSON/base64/argument faults retain HTTP 400. `phase=Envelope` covers parsing and command
construction; it changes to `Handler` immediately before command dispatch. Only the exception type
is written: JSON and handler messages can contain command input. No event contains an Authorization
value, credential, claim, caller identity, command key or command payload. Command execution outcomes
belong to Hosting.Resources, not this HTTP source.

**Contributor ownership adjustment (#1400).** A throwing contributor is caught and sanitized inside
Hosting.Resources. Existing contracts cannot carry its exception into this assembly without adding
a public exception member or a diagnostics forwarding API. The smallest internal hand-off is from
that catch to Hosting.Resources' own `HostingResourceEventSource.ContributorFailed`; this Web source
does not duplicate it. Health contributions and probe responses keep their fixed safe descriptions.
The producer's DESIGN documents the event, counter and exception-message policy. This departs from
the scope's requested Web-source ownership and is recorded for owner review, with no public API,
shipped visibility grant, or reverse dependency added.

These diagnostics add operator visibility relative to 10.0.0-preview.1; request status codes, token
acceptance and probe serialization are unchanged.
