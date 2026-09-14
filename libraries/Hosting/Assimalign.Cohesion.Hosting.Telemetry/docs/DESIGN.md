# Hosting telemetry design

## Closure and composition

Hosting.Telemetry is a sibling of Hosting.Resources. Its public references are Hosting, Hosting.Resources, Logging, Logging.Console and OpenTelemetry. The arrow never points back: COHAM001's project-graph and resolved-closure allowlists (build/Targets/Build.Rules.targets:201-205 and 252-257, errors 214-215 and 265-266) forbid Logging/OpenTelemetry in all eighteen ApplicationModel closures. The only additive Resources API is TryGetEnvironmentValue, approved by the final orchestrator ruling, exposing the existing invocation dictionary with no dependency.

## Gate and ownership

Without GatewayName and an endpoint, Configure returns false/null before touching a builder or allocating one. Hosts add no service or provider on that path. An enabled bootstrap installs the OpenTelemetry provider and returns an IHostService; every host registers that service first, to stop last. Database inserts it before prepopulated Options.Services too. Its StopAsync flushes then disposes the exporter with cancellation bounded to five seconds and the host stop token. For the seventeen new factories it also owns factory disposal, awaiting synchronous disposal on a worker only within that same budget. Web retains its existing builder and instance registration; its factory's lack of container disposal cannot lose the final export batch. Provider.DisposeCore is a secondary disposal path. Enabled hosts emit "Resource telemetry started" on start and "Resource telemetry stopping" immediately before the final flush.

## Environment and transport

ResourceContext.TryGetEnvironmentValue reads ambient invocation values for both topologies, never a process fallback. otlp-http selects JSON. Reserved otlp-grpc throws NotSupportedException: "TelemetryProtocol: otlp-grpc is reserved; this build serves OTLP over HTTP only." Unknown protocols and malformed configured URIs fail fast. Header files use ResourceMount.ReadAllBytes (including protected local-file unwrap); decoded buffers are zeroed. Blank/comment lines are ignored, duplicate names use the last value, empty files mean no headers. Secrets are never logged.

Headers are captured at bootstrap. Replacing the protected file during reconcile does not reload a running exporter; its host must restart before that credential expires (the gateway default lifetime is 24 hours). Live header refresh is deferred and must preserve the same protected-file read path.

SocketsHttpHandler uses ResourceContext.CreateOutboundTrustValidator; no anchors leaves platform validation intact. Explicit JSON console formatting is enabled only by log format json; an already-added Console provider wins. The builder exposes no enumeration, so the adapter catches its duplicate-name exception. No Logging API changes were required. Event maps to 10/EVENT; None is suppressed. Other mappings and Id/ParentId correlation are specified in OpenTelemetry/docs/DESIGN.md.

## Discovery, ordering and scope

The gateway injects only after a same-application LogSpace is Running with an observed otlp endpoint (Development may use a declared DevPort). There is no inferred DependsOn. A resource prepared first has no telemetry until a later preparation receives the resolved facts; updating an already-running process's environment requires restarting it. No-sink and self-export paths remove endpoint/protocol and the protected headers file. InProcessPlanController explicitly applies endpoint/protocol; InProcessContextFactory materializes the same protected headers file and supplies its path in ambient values.

Telemetry tokens have audience=LogSpace, subject=emitter and scope=telemetry, using a separate (application,sink,emitter) cache. They cannot authorize LogSpace query or management routes. RemoteReference sink discovery is deferred because IControlPlaneExternalResourceResolver does not supply a trusted named OTLP credential contract. Logs only; traces/metrics require missing Logging span/instrument primitives; protobuf and gRPC are deferred.
