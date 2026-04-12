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
- Nested service composition must be defined in L2 before L3 services are expected to host or compose other services through the runtime.
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
- Nested host composition is part of Runtime and must land before dependent L3 services are expected to compose other hosts.
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

- `libraries/Hosting/Assimalign.Cohesion.Hosting/src/Internal/HostToServiceWrapper.cs` and `libraries/Hosting/Assimalign.Cohesion.Hosting/src/Extensions/HostExtensions.cs` show the intended runtime model: a host can run as an `IHostService` inside another host.
- This means several L3 services are not truly independent top-level products. Some are service substrates that other services compose, while others are composed services that depend on those substrates.
- `ConfigurationStore` is a good example: its service host may depend on L2 runtime composition, web-facing admin endpoints, a database substrate, and identity contracts or `IdentityHub` depending on whether auth is delegated or embedded.
- The backlog should therefore distinguish:
- substrate initiatives such as web, database, shared identity contracts, and runtime composition
- composed service initiatives such as configuration, secrets, API management, scheduler, messaging channels, and control-plane services

## Hierarchy Prefix Scheme

- Use hierarchical title prefixes to reflect both layer and decomposition depth.
- Suggested pattern:
- initiative: `[L3.4] Cohesion - Operational Services`
- epic: `[L3.4.1] Configuration and Secrets`
- feature: `[L3.4.1.2] ConfigurationStore Hosting and Persistence`
- story: `[L3.4.1.2.1] Compose ConfigurationStore host with database persistence`
- Recommended interpretation:
- the first segment is the layer
- the second segment is the service family or substrate group
- the third segment is the epic within that family
- the fourth segment is the feature
- the fifth segment is the story
- This naming scheme is now applied across the GitHub backlog and should be preserved for future items.

## GitHub Project Update Rules

- Set `Priority` on every project item from `P001` through `P007`.
- Set `Start date` and `End date` on every initiative and epic. Features inherit the same window as their parent unless the feature is explicitly gated later.
- Use `Backlog` as the default status for all items until a real implementation session starts.
- Keep parent or sub-issue hierarchy for decomposition and use issue dependencies only for true execution blockers.
- If a placeholder service is split into multiple projects later, keep the existing service-level initiative and adjust epics or features rather than flattening the hierarchy.
