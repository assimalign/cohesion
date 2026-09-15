# Delivery Roadmap

## Purpose

This document is the execution map for the current GitHub backlog. It assigns priority, sequence, and dependency expectations so another model can work the project without guessing the critical path.

## Priority Scale

| Priority | Meaning | Usage |
|---|---|---|
| `P001` | Hard blockers | Foundation, AOT, runtime, and tooling work that everything else depends on |
| `P002` | Core platform | Web, operational stores, scheduler, and shared database engine |
| `P003` | Primary product services | SQL, DocumentDB, and IdentityHub |
| `P004` | Messaging core | MessageHub, EventHub, and ApiManager |
| `P005` | Secondary service families | Blob, cache, key-value, IoT, email, and notifications |
| `P006` | Advanced networking | DNS, Rezolvr, load balancing, NAT, and VPN |
| `P007` | Deferred or gated platform work | GraphDB and MediaHub work that depends on unresolved standards or upstream stabilization |

## Initiative Sequence

| Initiative | Priority | Start | End | Blocked By |
|---|---:|---:|---:|---|
| `[L1.1] Cohesion - Foundation and Support Libraries` | `P001` | `2026-04-13` | `2026-07-31` | none |
| `[L1.2] Cohesion - SDK, Tooling, and Delivery` | `P001` | `2026-04-20` | `2026-08-31` | Foundation |
| `[L2.1] Cohesion - Application Runtime and Composition` | `P001` | `2026-04-27` | `2026-08-15` | Foundation |
| `[L3.1] Cohesion - Web Platform` | `P002` | `2026-06-01` | `2026-10-15` | Foundation, Application Runtime |
| `[L3.2.1] Cohesion - Data Platform (Core Engine)` | `P002` | `2026-05-18` | `2026-09-15` | Foundation, Application Runtime |
| `[L3.2.2] Cohesion - Data Platform (SQL)` | `P003` | `2026-07-01` | `2026-11-30` | Database Core, Foundation |
| `[L3.2.3] Cohesion - Data Platform (DocumentDB)` | `P003` | `2026-07-15` | `2026-12-15` | Database Core, Foundation |
| `[L3.2.4] Cohesion - Data Platform (KV, Blob, Cache)` | `P005` | `2026-08-15` | `2027-01-31` | Database Core, Foundation |
| `[L3.2.5] Cohesion - Data Platform (GraphDB)` | `P007` | `2026-11-01` | `2027-03-31` | Database Core, Foundation, graph standard selection |
| `[L3.3] Cohesion - Identity Platform` | `P003` | `2026-07-15` | `2026-12-15` | Foundation, IdentityModel Foundation, Application Runtime, Operational Services |
| `[L3.4] Cohesion - Operational Services and Control Plane` | `P002` | `2026-06-15` | `2026-10-31` | Foundation, Application Runtime |
| `[L3.5] Cohesion - Messaging, Eventing, and Channels` | `P004` | `2026-08-01` | `2027-01-15` | Foundation, Application Runtime, Operational Services |
| `[L3.6] Cohesion - Networking and Edge` | `P006` | `2026-10-01` | `2027-03-15` | Foundation, Application Runtime, Operational Services |

## Epic Dependency Rules

- Every service-root initiative starts with its contract or engine epic before its client, hosting, or distribution epic.
- Runtime and AOT epics always complete before large service hosting or reflection-sensitive features.
- L2 provides the concrete `<Area>.Hosting` builders' `AddService` verb (O34) over nestable `IHost` and `Gateway.InProcess` composite realization; L3 services compose through those contracts.
- Database model epics depend on the shared database core storage and execution epics.
- Identity federation depends on the shared `IdentityModel` foundation, the token or key epic, and the operational services initiative for secure config and secret handling.
- Eventing, IoT, email, and notifications depend on the core messaging primitives and on operational observability.
- Networking edge work starts only after DNS protocol contracts and `Rezolvr` server roles are defined.
- MediaHub expansion stays gated until content and parser stabilization work in Foundation is complete.

## Feature Ordering Rules

- Language or contract features come before engine or storage features.
- Storage features come before replication and client features.
- Security and governance features should run in parallel with core engine work, not after it.
- Compliance suites start as soon as the first protocol contract is declared, not after the implementation is done.
- AOT smoke tests are attached to each area as soon as a representative host or client exists.

## GitHub Dependency Map

Use issue dependencies to encode the real execution path in GitHub:

- Runtime is blocked by Foundation.
- Nested host composition is delivered in Runtime through the concrete `<Area>.Hosting` builders' `AddService` verb (item 9, revised by O34) and `Gateway.InProcess` (item 24); dependent L3 services use those seams.
- SDK is blocked by Foundation.
- Database Core is blocked by Runtime and Foundation.
- Web is blocked by Runtime and Foundation.
- Operational Services is blocked by Runtime and Foundation.
- SQL is blocked by Database Core.
- DocumentDB is blocked by Database Core.
- IdentityHub is blocked by IdentityModel Foundation, Runtime, and Operational Services.
- Messaging is blocked by Runtime and Operational Services.
- KV, Blob, and Cache are blocked by Database Core.
- Networking and Edge are blocked by Operational Services and Runtime.
- GraphDB is blocked by Database Core and graph standard selection.

## Service-to-Service Design Dependencies

- `ConfigurationStore`, `SecretStore`, and `LogSpace` must be usable by `IdentityHub`, `ApiManager`, `Scheduler`, `MessageHub`, `EventHub`, and `Web`.
- `IdentityModel` must provide the shared tenant, principal, credential, token, claim, and validation contracts that `IdentityHub` builds on; `IdentityHub` should not create a competing foundational identity model.
- `IdentityHub` should provide reusable auth contracts to `Web`, `ApiManager`, `MessageHub`, and `NotificationHub`.
- `Dns` should provide reusable DNS protocol and record-model primitives.
- `Rezolvr` should be treated as a standalone DNS server product built on DNS primitives, not as a generic service-discovery subsystem for messaging or other services.
- `MessageHub` and `EventHub` should provide channel primitives to `NotificationHub`, `EmailHub`, and `IoTHub` instead of each service re-inventing queueing.
- `ApiManager` should consume `Web`, `IdentityHub`, `OpenApi`, `ConfigurationStore`, and `SecretStore`.

## Nested Service Composition

- The public composition model is the concrete `<Area>.Hosting` builder's `AddService` verb (O34; root contracts reference no hosting library) over nestable `IHost` (item 9, `51045965`; design note `4ef29290`). `HostToServiceWrapper.cs` and `HostExtensions.cs` in `libraries/Hosting/Assimalign.Cohesion.Hosting/src/` are the underlying host-as-service plumbing.
- `Assimalign.Cohesion.ApplicationModel.Gateway.InProcess` realizes composite members through their real application entry points (item 24, `a03cfcf8`); its [package design](../libraries/ApplicationModel/Assimalign.Cohesion.ApplicationModel.Gateway.InProcess/docs/DESIGN.md) owns the lifecycle and resource-context contract.
- The built layers are L1 foundation plus SDK/tooling; L2 application runtime and composition (`ApplicationModel`, the gateway family, and `Hosting`); and L3 the 18 service platforms.
- Service dependencies remain explicit graph relationships; the composite's platform-neutral plan belongs to the base ApplicationModel planner.

## WBS Title Scheme and Backlog Dispositions

- [Workflow rules](../.claude/rules/workflow.md) define `[<wbs>] <title>` in Project #13: area epic `L01.01.NN`, feature `L01.01.NN.MM`, task `L01.01.NN.MM.PP`.
- The initiative table's `[L3.x]` titles are legacy display names, mapped to WBS ids by the [developer-experience design](DEVELOPER_EXPERIENCE_DESIGN.md) §10 issue map; retain those table rows as historical scheduling context.
- Program epics #17/#18/#19 and #130–#132 are superseded by that design and its §13 `[R]` decisions.
- Item 41's remaining dispositions were already applied in GitHub during the 2026-09-07 review: #23/#139–#141 delivered; #21 re-titled "InProcess gateway"; #304 maps to item 9; #305 satisfied; #306/#307 folded into item 9. This records that review; it does not perform backlog mutations.

## GitHub Project Update Rules

- Set `Priority` on every project item from `P001` through `P007`.
- Set `Start date` and `End date` on every initiative and epic. Features inherit the same window as their parent unless the feature is explicitly gated later.
- Use `Backlog` as the default status for all items until a real implementation session starts.
- Keep parent or sub-issue hierarchy for decomposition and use issue dependencies only for true execution blockers.
- If a placeholder service is split into multiple projects later, keep the existing service-level initiative and adjust epics or features rather than flattening the hierarchy.
