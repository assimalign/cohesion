# Service Layer Design

## Purpose

This document defines the target high-level design for each `resources/*` service folder so implementation work can proceed with a stable service shape, explicit abstractions, and a readable dependency tree.

## Shared Design Rules

- Every service keeps `Assimalign.Cohesion.{Service}` as the base library for public abstractions, shared value objects, extension members, and service-wide contracts.
- Child projects inside a service inherit from the root service library instead of cross-linking sideways wherever possible. This keeps dependency trees readable and matches the current Cohesion pattern.
- Placeholder folders and placeholder projects are not the final modular split. Add projects when the feature set or dependency direction demands it.
- Service composition is allowed and should be modeled explicitly. A service host may run another host as a nested `IHostService` through `HostExtensions.AsService()` and `HostToServiceWrapper`, so backlog design should distinguish substrate services from composed services.
- Every service must be NativeAOT-compatible. Prefer explicit registration, source generation, static metadata, and linker-safe serialization paths.
- Every service needs at least six test slices: unit tests, integration tests, contract tests, AOT publish smoke tests, performance or soak tests for hot paths, and failure-path tests.
- Formal-spec services need two additional suites: compliance tests for the governing standard and interoperability tests against representative external implementations or corpus files.

## Cross-Service Library Additions

- Add `libraries/Core/Assimalign.Cohesion.Serialization` for AOT-safe JSON or binary serialization contracts and source-generated metadata.
- Add `libraries/Core/Assimalign.Cohesion.Security` for common policy, permission, secret-reference, and key-identifier primitives that should not live inside one service.
- Add `libraries/Core/Assimalign.Cohesion.Diagnostics` for health states, problem details, correlation, audit-event envelopes, and operational result contracts shared across services.
- Expand `libraries/IdentityModel` into a real foundation library for tenants, users, groups, applications, service principals, sessions, credentials, claims, protocol descriptors, and validation contracts. This is a hard blocker for `IdentityHub`.
- Extend `libraries/Hosting` to include reusable readiness, liveness, graceful-shutdown, drain, and background-work coordination primitives that services can reuse instead of re-implementing.
- Extend `libraries/Hosting` to finish the nested-host composition surface around `IHostBuilder`, explicit hosted-service registration, dependency ordering, and lifecycle propagation for host-as-service scenarios.
- Extend `libraries/OpenTelemetry` with common telemetry conventions for service name, tenant, correlation, command, query, and pipeline spans used by multiple services.

## Service Design

## ApiManager

Current state:
- Placeholder. Two projects exist and both are still `Class1.cs` level.

Root abstractions:
- Reuse root project `Assimalign.Cohesion.ApiManager`.
- Add `IApiManager`, `IApiGateway`, `IApiBackend`, `IApiPolicy`, `IApiProduct`, `IApiSubscription`, `IApiRouteContract`, and `IApiDocumentSource`.

Project plan:
- Keep `Assimalign.Cohesion.ApiManager` as the root abstraction library.
- Keep `Assimalign.Cohesion.ApiManager.Hosting` for runtime integration.
- Add `Assimalign.Cohesion.ApiManager.Gateway` for route matching, backend selection, transformation, and proxy behavior.
- Add `Assimalign.Cohesion.ApiManager.Policies` for auth, rate-limit, quota, retry, rewrite, and header policy evaluation.
- Add `Assimalign.Cohesion.ApiManager.Contracts` for OpenAPI import, contract diffing, and capability descriptors.
- Add `Assimalign.Cohesion.ApiManager.Security` for keys, authz, product subscriptions, and admin-plane protection.

Specifications:
- OpenAPI 3.1: <https://spec.openapis.org/oas/v3.1.0>
- JSON Schema 2020-12: <https://json-schema.org/draft/2020-12>
- OAuth 2.0 framework: <https://datatracker.ietf.org/doc/html/rfc6749>
- OpenID Connect Core: <https://openid.net/specs/openid-connect-core-1_0.html>

Stories:
- Feature: backend registration and route resolution.
- Story: define backend contracts, route predicates, and transform descriptors in the root library.
- Feature: gateway policy pipeline.
- Story: implement auth, rate-limit, quota, and rewrite policy evaluation in a deterministic order.
- Feature: contract import and publication.
- Story: import OpenAPI documents, diff revisions, and validate backend compatibility.

## ConfigurationStore

Current state:
- Root abstractions exist but the service is still thin and test coverage is placeholder-level.

Root abstractions:
- Reuse `IConfigurationStoreApplication`, `IConfigurationStoreApplicationBuilder`, and `IConfigurationStoreLoader`.
- Add `IConfigurationStore`, `IConfigurationNamespace`, `IConfigurationSnapshot`, `IConfigurationRevision`, `IConfigurationWatch`, and `IConfigurationPromotionPolicy`.

Project plan:
- Keep `Assimalign.Cohesion.ConfigurationStore` as the root abstraction library.
- Add `Assimalign.Cohesion.ConfigurationStore.Client` for read/write client APIs.
- Add `Assimalign.Cohesion.ConfigurationStore.Storage` for versioned storage and indexing.
- Add `Assimalign.Cohesion.ConfigurationStore.Hosting` for hosted service integration.
- Add `Assimalign.Cohesion.ConfigurationStore.Security` for access policy, protected values, and audit.
- Add `Assimalign.Cohesion.ConfigurationStore.Replication` if cross-region or multi-node sync is required.

Specifications:
- JSON Patch: <https://datatracker.ietf.org/doc/html/rfc6902>
- JSON Merge Patch: <https://datatracker.ietf.org/doc/html/rfc7386>

Stories:
- Feature: versioned configuration namespaces and snapshots.
- Story: implement immutable revisions, labels, promotion flows, and rollback.
- Feature: watchers and runtime reload.
- Story: define polling or push watch contracts and generation-safe change notifications.
- Feature: governance and audit.
- Story: add approvals, diff history, retention, and protected-setting policy enforcement.

## Database

Current state:
- Broadest service area. SQL parser and several data-model packages exist, but shared engine and some storage areas are incomplete or placeholder-driven.

Root abstractions:
- Reuse `IDatabase`, `IDatabaseEngine`, `IDatabaseSession`, `IDatabaseTransaction`, `IStorage`, `IQueryExecutor`, `IDocumentDatabase`, `IGraphDatabase`, `IKeyValueDatabase`, and `ISqlDatabase`.
- Add `IDatabaseHost`, `ITransactionLog`, `IRecoveryPolicy`, `IReplicationLog`, `ICatalogStore`, and `IQueryPlan`.

Project plan:
- Keep `Assimalign.Cohesion.Database` as the service root.
- Keep shared engine projects already present: `Storage`, `Execution`, `Governance`, `Hosting`, `Replication`, `Security`, and `Types`.
- Keep model roots already present: `Sql`, `Documents`, `Graph`, `KeyValuePair`, `Blob`, `Cache`, `Embedded`, `Memory`.
- Add `Assimalign.Cohesion.Database.Storage.Journal` if journaling becomes large enough to deserve its own module.
- Add `Assimalign.Cohesion.Database.Conformance` for spec or corpus suites shared by SQL and OQL.

Specifications:
- SQL dialect contract must be authored internally, with any ANSI or ISO alignment declared explicitly.
- OQL compatibility target should be stated explicitly before expansion.
- Graph query standard must be selected before deep implementation.

Stories:
- Feature: shared storage engine, buffer pool, journaling, recovery, backup, and restore.
- Story: finish the shared engine before growing model-specific execution stacks.
- Feature: SQL dialect contract, conformance corpus, schema catalog, planning, and execution.
- Story: declare the supported SQL surface and lock tests to it.
- Feature: document query model, indexing, replication, and client behavior.
- Story: formalize OQL support and tie storage design to document semantics.
- Feature: graph standard selection and graph-native schema or traversal model.
- Story: choose the graph query contract before storage or client expansion.
- Feature: key-value, blob, and cache model semantics.
- Story: keep cache coherence and blob metadata explicit rather than treating them as generic storage wrappers.

## EmailHub

Current state:
- One service project plus tests, currently shallow.

Root abstractions:
- Reuse `IEmailHub`.
- Add `IEmailMessage`, `IEmailEnvelope`, `IEmailTemplate`, `IEmailTransport`, `IEmailReceipt`, and `ISuppressionPolicy`.

Project plan:
- Keep `Assimalign.Cohesion.EmailHub` as the root library.
- Add `Assimalign.Cohesion.EmailHub.Client` for application-facing send APIs.
- Add `Assimalign.Cohesion.EmailHub.Smtp` for SMTP transport.
- Add `Assimalign.Cohesion.EmailHub.Templates` for rendering and localization.
- Add `Assimalign.Cohesion.EmailHub.Tracking` for delivery, bounce, complaint, and open or click telemetry if in scope.

Specifications:
- SMTP: <https://datatracker.ietf.org/doc/html/rfc5321>
- Internet Message Format: <https://datatracker.ietf.org/doc/html/rfc5322>
- MIME Part One: <https://datatracker.ietf.org/doc/html/rfc2045>
- DKIM: <https://datatracker.ietf.org/doc/html/rfc6376>
- SPF: <https://datatracker.ietf.org/doc/html/rfc7208>
- DMARC: <https://datatracker.ietf.org/doc/html/rfc7489>

Stories:
- Feature: message composition, template rendering, attachments, and localization.
- Story: define message contracts that can be rendered without runtime reflection.
- Feature: SMTP transport and delivery tracking.
- Story: add send, retry, bounce, complaint, and suppression flows with protocol tests.
- Feature: compliance and interoperability.
- Story: build MIME, header, and SMTP corpora for compliance and malformed-message validation.

## EventHub

Current state:
- Single root project and no meaningful supporting structure yet.

Root abstractions:
- Reuse `IEventHub`.
- Add `IEventStream`, `IEventPublisher`, `IEventConsumer`, `IConsumerGroup`, `ICheckpointStore`, and `IEventPartition`.

Project plan:
- Keep `Assimalign.Cohesion.EventHub` as the root library.
- Add `Assimalign.Cohesion.EventHub.Client`.
- Add `Assimalign.Cohesion.EventHub.Storage` for retention, checkpoints, offsets, and replay.
- Add `Assimalign.Cohesion.EventHub.Hosting` for hosted processors.
- Add `Assimalign.Cohesion.EventHub.Contracts` for event envelope contracts and CloudEvents integration.

Specifications:
- CloudEvents 1.0: <https://github.com/cloudevents/spec>
- AMQP 1.0 if AMQP transport is used.

Stories:
- Feature: partitioned streams, consumer groups, retention, and replay.
- Story: define offsets, checkpoints, and replay semantics before client work expands.
- Feature: event envelope contracts.
- Story: align event metadata with CloudEvents where applicable.
- Feature: hosted processors.
- Story: implement checkpointing, rebalance, and recovery for long-running consumers.

## IdentityHub

Current state:
- Strongest design artifact exists here already, but the project split is still too small for the scope.
- `IdentityHub` is blocked by the current incompleteness of `libraries/IdentityModel`. The identity service should not invent its own foundational object model if the shared identity contracts are still partial.

Root abstractions:
- Reuse `IIdentityHub`, `IIdentityClient`, `IIdentityProvider`, `IIdentityContext`, `IIdentityResult`, and related client abstractions.
- Add `ITokenService`, `IKeyStore`, `IDirectoryStore`, `ISessionStore`, `IFederationProvider`, `IAuditStore`, and `IProvisioningService`.

Project plan:
- Keep `Assimalign.Cohesion.IdentityHub` as the root service library.
- Keep `Assimalign.Cohesion.IdentityHub.Models` for shared data contracts.
- Add `Assimalign.Cohesion.IdentityHub.Tokens` for JWT, JWS, JWE, JWK, and JWA flows.
- Add `Assimalign.Cohesion.IdentityHub.Directory` for tenants, users, groups, apps, and service principals.
- Add `Assimalign.Cohesion.IdentityHub.Federation` for OAuth, OIDC, SAML, and external identity provider integration.
- Add `Assimalign.Cohesion.IdentityHub.Sessions` for refresh tokens, device sessions, and revocation.
- Add `Assimalign.Cohesion.IdentityHub.Provisioning` for SCIM if provisioning remains in scope.
- Add `Assimalign.Cohesion.IdentityHub.Storage` if persistence moves beyond a simple directory module.

Specifications:
- JWS: <https://datatracker.ietf.org/doc/html/rfc7515>
- JWE: <https://datatracker.ietf.org/doc/html/rfc7516>
- JWK: <https://datatracker.ietf.org/doc/html/rfc7517>
- JWA: <https://datatracker.ietf.org/doc/html/rfc7518>
- JWT: <https://datatracker.ietf.org/doc/html/rfc7519>
- OAuth 2.0 Authorization Framework: <https://datatracker.ietf.org/doc/html/rfc6749>
- OAuth 2.0 Authorization Server Metadata: <https://datatracker.ietf.org/doc/html/rfc8414>
- PKCE: <https://datatracker.ietf.org/doc/html/rfc7636>
- OpenID Connect Core: <https://openid.net/specs/openid-connect-core-1_0.html>
- SAML 2.0: <https://docs.oasis-open.org/security/saml/v2.0/>
- SCIM 2.0: <https://datatracker.ietf.org/doc/html/rfc7643> and <https://datatracker.ietf.org/doc/html/rfc7644>

Stories:
- Feature: token issuance, validation, metadata, and key rotation.
- Story: lock supported algorithms and metadata behavior with compliance suites.
- Feature: directory model for tenants, users, groups, apps, principals, and audit.
- Story: align model shape with the existing design document and add session or audit semantics explicitly.
- Feature: federation and provisioning.
- Story: support declared OAuth, OIDC, SAML, and SCIM flows with interop suites and abuse-case tests.

## IoTHub

Current state:
- Root and hosting projects exist but there is almost no implementation.

Root abstractions:
- Reuse `IIoTHub`.
- Add `IDeviceRegistry`, `IDeviceIdentity`, `ITelemetryIngress`, `ICommandDispatcher`, `IDeviceTwinStore`, and `IDeviceProvisioningService`.

Project plan:
- Keep `Assimalign.Cohesion.IoTHub` as the root library.
- Keep `Assimalign.Cohesion.IoTHub.Hosting`.
- Add `Assimalign.Cohesion.IoTHub.Client`.
- Add `Assimalign.Cohesion.IoTHub.Protocols.Mqtt`.
- Add `Assimalign.Cohesion.IoTHub.Protocols.Amqp` if AMQP remains in scope.
- Add `Assimalign.Cohesion.IoTHub.Devices` for registry and provisioning.
- Add `Assimalign.Cohesion.IoTHub.Twins` for digital twin or shadow state.

Specifications:
- MQTT 3.1.1: <https://docs.oasis-open.org/mqtt/mqtt/v3.1.1/>
- MQTT 5.0: <https://docs.oasis-open.org/mqtt/mqtt/v5.0/>
- AMQP 1.0 if used.

Stories:
- Feature: device registry and provisioning.
- Story: define identity, enrollment, rotation, and disablement flows.
- Feature: telemetry ingress and command dispatch.
- Story: support device-to-cloud and cloud-to-device flows with protocol compliance tests.
- Feature: twin or shadow state.
- Story: define patch, desired-state, and reported-state semantics before storage work expands.

## LoadBalancer

Current state:
- Placeholder single project.

Root abstractions:
- Add `ILoadBalancer`, `IBackendPool`, `IHealthProbe`, `IRoutePolicy`, `ISessionAffinityPolicy`, and `ILoadBalancingDecision`.

Project plan:
- Keep `Assimalign.Cohesion.LoadBalancer` as the root library.
- Add `Assimalign.Cohesion.LoadBalancer.ControlPlane`.
- Add `Assimalign.Cohesion.LoadBalancer.DataPlane`.
- Add `Assimalign.Cohesion.LoadBalancer.Hosting`.

Specifications:
- HTTP semantics if L7 behavior is supported: <https://datatracker.ietf.org/doc/html/rfc9110>
- Any supported LB or proxy protocol must be declared before compliance work is started.

Stories:
- Feature: backend pools, health probes, and failover decisions.
- Story: define weighted, priority, and round-robin policies.
- Feature: control plane.
- Story: add route policy, affinity, drain, and maintenance workflows.
- Feature: operational validation.
- Story: cover failover, drain, and recovery behavior under load.

## LogSpace

Current state:
- Root project plus telemetry project, but almost no surface implemented.

Root abstractions:
- Add `ILogSpace`, `ILogIngestor`, `ILogQueryService`, `ILogArchive`, `ILogRetentionPolicy`, and `ILogCursor`.

Project plan:
- Keep `Assimalign.Cohesion.LogSpace` as the root library.
- Keep `Assimalign.Cohesion.LogSpace.Telemetry` for ingestion helpers.
- Add `Assimalign.Cohesion.LogSpace.Storage`.
- Add `Assimalign.Cohesion.LogSpace.Query`.
- Add `Assimalign.Cohesion.LogSpace.Client`.

Specifications:
- OpenTelemetry logs and traces guidance should be followed where telemetry is ingested.

Stories:
- Feature: ingestion, retention, and query contracts.
- Story: define append-only ingest, index strategy, retention, and archive semantics.
- Feature: telemetry integration.
- Story: map spans, logs, and metrics into a coherent operational schema.
- Feature: query and export.
- Story: add cursor-based retrieval, filters, correlation lookup, and export pipelines.

## MediaHub

Current state:
- Placeholder single project. This service is strategically gated by the state of the content libraries.

Root abstractions:
- Reuse `IMediaHub`.
- Add `IMediaAssetStore`, `IMediaPipeline`, `ITranscodeJob`, `IPackageManifest`, and `IMediaDeliveryPolicy`.

Project plan:
- Keep `Assimalign.Cohesion.MediaHub` as the root library.
- Add `Assimalign.Cohesion.MediaHub.Assets`.
- Add `Assimalign.Cohesion.MediaHub.Jobs`.
- Add `Assimalign.Cohesion.MediaHub.Packaging`.
- Add `Assimalign.Cohesion.MediaHub.Delivery` only after content libraries are stable.

Specifications:
- HLS: <https://datatracker.ietf.org/doc/html/rfc8216>
- Any additional container or packaging standards must be declared per retained workflow.

Stories:
- Feature: asset metadata and job orchestration.
- Story: define upload, ingest, catalog, and processing contracts first.
- Feature: packaging and manifests.
- Story: support only declared media outputs after content-format libraries are stable.
- Feature: delivery policy.
- Story: add signed URL, DRM, or CDN integration only after asset and packaging layers are real.

## MessageHub

Current state:
- Root and client projects exist, but the broker surface is still shallow.

Root abstractions:
- Reuse `IMessageHub` and `IMessageHubClient`.
- Add `IMessageQueue`, `IMessageTopic`, `IMessageProducer`, `IMessageConsumer`, `ISettlementContext`, and `IMessageEnvelope`.

Project plan:
- Keep `Assimalign.Cohesion.MessageHub` as the root library.
- Keep `Assimalign.Cohesion.MessageHub.Client`.
- Add `Assimalign.Cohesion.MessageHub.Broker`.
- Add `Assimalign.Cohesion.MessageHub.Hosting`.
- Add `Assimalign.Cohesion.MessageHub.Protocols.Amqp`.

Specifications:
- AMQP 1.0: <https://docs.oasis-open.org/amqp/core/v1.0/>

Stories:
- Feature: queues, topics, dead-lettering, retry, and idempotency.
- Story: define broker semantics before transport or client convenience APIs expand.
- Feature: client settlement and diagnostics.
- Story: expose explicit ack, nack, defer, retry, and correlation semantics.
- Feature: compliance and interop.
- Story: run wire-level interop checks against declared AMQP behaviors.

## NatGateway

Current state:
- Small project with placeholder-level tests.

Root abstractions:
- Reuse `INatGateway`.
- Add `INatRule`, `INatSessionTable`, `IPortAllocator`, `IEgressPolicy`, and `INatTranslation`.

Project plan:
- Keep `Assimalign.Cohesion.NatGateway` as the root library.
- Add `Assimalign.Cohesion.NatGateway.ControlPlane`.
- Add `Assimalign.Cohesion.NatGateway.DataPlane`.
- Add `Assimalign.Cohesion.NatGateway.Diagnostics`.

Specifications:
- Supported NAT behaviors must be declared before implementation. There is no single free-standing protocol spec equivalent to HTTP or DNS, so Cohesion must publish the supported behavior matrix.

Stories:
- Feature: NAT translation and session lifecycle.
- Story: define SNAT, DNAT, timeout, and reuse behavior.
- Feature: policy and observability.
- Story: add control-plane rule management and diagnostics for live translations.
- Feature: scale and failure behavior.
- Story: validate port exhaustion, failover, and drain scenarios.

## NotificationHub

Current state:
- Root and client projects exist, but no meaningful delivery model yet.

Root abstractions:
- Reuse `INotificationHub`.
- Add `INotificationMessage`, `INotificationSubscription`, `INotificationChannel`, `INotificationTemplate`, and `IDeliveryReceiptStore`.

Project plan:
- Keep `Assimalign.Cohesion.NotificationHub` as the root library.
- Keep `Assimalign.Cohesion.NotificationHub.Client`.
- Add `Assimalign.Cohesion.NotificationHub.Subscriptions`.
- Add `Assimalign.Cohesion.NotificationHub.Templates`.
- Add `Assimalign.Cohesion.NotificationHub.Channels.WebPush`.
- Add other channel projects only after their standards and provider contracts are declared.

Specifications:
- Web Push: <https://datatracker.ietf.org/doc/html/rfc8030>
- VAPID: <https://datatracker.ietf.org/doc/html/rfc8292>

Stories:
- Feature: subscriptions and audience targeting.
- Story: define user, device, topic, and tenant subscription contracts.
- Feature: template and channel routing.
- Story: support per-channel transforms and delivery policies.
- Feature: delivery receipts and suppression.
- Story: capture send outcomes, retries, and terminal failures across channels.

## Rezolvr

Current state:
- Placeholder service with `Class1.cs`.
- `Rezolvr` is intended to be a standalone DNS server product. It should not be modeled as a generic service-discovery registry or as a messaging-adjacent runtime helper.

Root abstractions:
- Add `IDnsServer`, `IDnsZoneStore`, `IDnsRecordProvider`, `IDnsForwarder`, `IDnsRequestHandler`, and `IDnsZoneTransferPolicy`.

Project plan:
- Keep `Assimalign.Cohesion.Rezolvr` as the root library.
- Add `Assimalign.Cohesion.Rezolvr.Admin`.
- Add `Assimalign.Cohesion.Rezolvr.Hosting`.
- Add `Assimalign.Cohesion.Rezolvr.Storage` for zones, records, forwarding, and runtime state.
- Add `Assimalign.Cohesion.Rezolvr.Recursion` if recursive or forwarding behavior becomes complex enough to split.

Specifications:
- DNS Concepts and Facilities: <https://datatracker.ietf.org/doc/html/rfc1034>
- DNS Implementation and Specification: <https://datatracker.ietf.org/doc/html/rfc1035>
- EDNS(0): <https://datatracker.ietf.org/doc/html/rfc6891>
- AXFR: <https://datatracker.ietf.org/doc/html/rfc5936>

Stories:
- Feature: authoritative DNS server and zone storage.
- Story: implement zones, records, updates, and server request handling as first-class DNS server behavior.
- Feature: forwarding and recursive resolution.
- Story: define forwarding, recursion, cache, and timeout behavior only after the supported DNS server roles are declared.
- Feature: admin and operations.
- Story: add administrative contracts for zone management, metrics, transfers, and diagnostics.

## Scheduler

Current state:
- Useful core abstractions exist, but timer and hosting projects still contain placeholders and several `NotImplementedException` paths remain.

Root abstractions:
- Reuse `IScheduler`, `ISchedulerBuilder`, `ISchedule`, `IScheduleContext`, `IScheduleJob`, and `IScheduleProvider`.
- Add `IScheduleTrigger`, `IJobStore`, `IMisfirePolicy`, `IExecutionLease`, and `IScheduleCoordinator`.

Project plan:
- Keep `Assimalign.Cohesion.Scheduler` as the root library.
- Keep `Assimalign.Cohesion.Scheduler.Cron`, `Hosting`, and `Timer`.
- Add `Assimalign.Cohesion.Scheduler.Storage`.
- Add `Assimalign.Cohesion.Scheduler.Distributed` if clustered execution remains in scope.

Specifications:
- If recurrence grows beyond cron, use iCalendar recurrence rules: <https://datatracker.ietf.org/doc/html/rfc5545>
- Cron grammar must be published internally because there is no single canonical cron standard.

Stories:
- Feature: durable schedules, retries, and misfire handling.
- Story: define schedule persistence, recovery, and failure semantics before clustering.
- Feature: cron and timer execution.
- Story: lock the cron grammar and cover parser or schedule behavior with compliance tests.
- Feature: hosted and distributed execution.
- Story: add worker coordination, leader election, and duplicate-prevention only after the local scheduler core is solid.

## SecretStore

Current state:
- Root and client projects exist, but storage and policy layers are missing.

Root abstractions:
- Reuse `ISecretStore` and `ISecretStoreClient`.
- Add `ISecretVersion`, `ISecretLease`, `ISecretPolicy`, `ISecretRotationStrategy`, and `ISecretAuditEvent`.

Project plan:
- Keep `Assimalign.Cohesion.SecretStore` as the root library.
- Keep `Assimalign.Cohesion.SecretStore.Client`.
- Add `Assimalign.Cohesion.SecretStore.Storage`.
- Add `Assimalign.Cohesion.SecretStore.Rotation`.
- Add `Assimalign.Cohesion.SecretStore.Hosting`.

Specifications:
- No single IETF spec governs secret-store behavior, so Cohesion must publish a service contract for versioning, leasing, rotation, and audit.

Stories:
- Feature: versioned secret storage and policy.
- Story: implement create, read, rotate, disable, destroy, and recover flows.
- Feature: lease and rotation handling.
- Story: add short-lived secret or credential leasing and renewal policies if in scope.
- Feature: audit and access policy.
- Story: capture access events, policy decisions, and break-glass workflows.

## VpnGateway

Current state:
- No projects exist yet.

Root abstractions:
- Add `IVpnGateway`, `IVpnTunnel`, `IVpnPeer`, `IKeyExchangePolicy`, `IRouteAdvertisement`, and `IVpnSession`.

Project plan:
- Add `Assimalign.Cohesion.VpnGateway` as the root library.
- Add `Assimalign.Cohesion.VpnGateway.ControlPlane`.
- Add `Assimalign.Cohesion.VpnGateway.DataPlane`.
- Add protocol-specific child projects only after the supported protocol set is chosen.

Specifications:
- WireGuard protocol docs if WireGuard is selected.
- IKEv2 and IPsec RFCs if standards-based site-to-site or client VPN support is selected.

Stories:
- Feature: protocol selection and gateway contract.
- Story: publish the supported VPN model before building the data plane.
- Feature: tunnel lifecycle and policy.
- Story: define peer, key, route, and session behavior.
- Feature: interoperability.
- Story: build interop suites only after the protocol matrix is fixed.

## Web

Current state:
- Strongest service after Database and IdentityHub. Routing and hosting have real code, but the full web platform is not complete yet.

Root abstractions:
- Reuse `IWebApplication`, `IWebApplicationBuilder`, `IWebApplicationContext`, `IWebApplicationMiddleware`, `IWebApplicationPipeline`, `IWebApplicationPipelineBuilder`, `IWebApplicationServer`, `IWebApplicationServerManager`, `IRouter`, `IRouterBuilder`, `IRouterRoute`, and `IRouterRouteHandler`.
- Add `IEndpointDescriptor`, `IRequestContext`, `IResponseWriter`, `IResultExecutor`, `IAuthenticationHandler`, `IAuthorizationPolicyEvaluator`, and `IOpenApiDescriptorProvider`.

Project plan:
- Keep the existing root and child projects: `Web`, `Api`, `Api.Controllers`, `ApplicationModel`, `Authentication`, `Authorization`, `CookiePolicy`, `Cors`, `Functions`, `Hosting`, `Results`, and `Routing`.
- Add `Assimalign.Cohesion.Web.Metadata` if endpoint metadata and OpenAPI descriptors become large enough to deserve their own module.
- Add `Assimalign.Cohesion.Web.ProblemDetails` if standardized API error handling becomes a large surface.

Specifications:
- HTTP Semantics: <https://datatracker.ietf.org/doc/html/rfc9110>
- HTTP/1.1 Messaging: <https://datatracker.ietf.org/doc/html/rfc9112>
- HTTP/2: <https://datatracker.ietf.org/doc/html/rfc9113>
- HTTP/3: <https://datatracker.ietf.org/doc/html/rfc9114>
- URI Syntax: <https://datatracker.ietf.org/doc/html/rfc3986>
- Cookies: <https://datatracker.ietf.org/doc/html/rfc6265>
- OpenAPI 3.1: <https://spec.openapis.org/oas/v3.1.0>
- Problem Details: <https://datatracker.ietf.org/doc/html/rfc9457>

Stories:
- Feature: request pipeline, transport bridge, graceful shutdown, and diagnostics.
- Story: align the web runtime fully with the canonical application runtime.
- Feature: routing, endpoint metadata, results, controllers, and functions.
- Story: move metadata and docs generation toward explicit descriptors and source-generation-friendly contracts.
- Feature: browser and API security.
- Story: implement authentication, authorization, cookie policy, and CORS with standards-oriented compliance tests.
