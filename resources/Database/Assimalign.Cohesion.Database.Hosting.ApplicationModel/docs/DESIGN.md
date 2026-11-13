# Assimalign.Cohesion.Database.Hosting.ApplicationModel — Design

> **Status (2026-11-02, #1785):** Database joins the restarted Application Model through the
> EmailHub integration's shape. The instance-side contracts and decisions are
> [ApplicationModel.Hosting](../../../../libraries/ApplicationModel/Assimalign.Cohesion.ApplicationModel.Hosting/docs/DESIGN.md);
> [Database.Hosting](../../Assimalign.Cohesion.Database.Hosting/docs/DESIGN.md) owns engine construction and host ordering.

## Purpose

`builder.Services.AddResource(name, resource => ...)` makes an ordinary Database application a
managed resource instance. Its selected platform client publishes its manifest, restores the
state the gateway kept, and carries the gateway's calls to the instance. With no named platform,
the application runs unmanaged with its code-defined state and AddResource registers nothing.
An environment naming a platform the application did not opt into is refused at registration.

## References

The package references Database, Database.Hosting, DependencyInjection, Hosting and
ApplicationModel.Hosting only. COHRES001 sanctions the exact runtime-module reference by the
integration package's name, with no exemption property. Applications reference the platform
hosting packages they opt into; the integration carries no transport or gateway. Arrows mean
"references".

```mermaid
flowchart LR
    Integration["Database.Hosting.ApplicationModel"] --> Model["ApplicationModel.Hosting"]
    Integration --> Runtime["Database.Hosting"]
    Integration --> Root["Database — area root"]
    Integration --> DI["DependencyInjection"]
    Integration --> Hosting["Hosting"]
    Runtime --> Root
    Runtime --> Hosting
    Model --> Hosting
    App["The application's Program.cs"] --> Integration
    App --> Platform["ApplicationModel.Hosting.InProcess"]
    Platform --> Model
```

## Composition

A managed AddResource call registers one singleton `IHostService` factory. Database.Hosting runs
it once when Build invokes the container's lifecycle factories, after freezing the engine/server registry
and creating all AddService products. It creates these pieces:

| Piece | Role |
| --- | --- |
| `DatabaseResourceOptions` | A sealed leaf of `ApplicationInstanceOptions`, adding only `Name`. The base supplies platform selection, deployment, AddAction, JsonContexts and CreateActions. |
| `DatabaseResource` (internal) | An `IApplicationInstance` with provider `Database`, the resource name, a new instance key, the environment's deployment, exactly the added actions in order, and health contributors mapped from Hosting's observations. |
| `ApplicationInstanceControlPlane` | The actions and their state, with JSON content resolved through the added actions' source-generated contexts. Duplicate action names fail composition. |
| `ApplicationInstanceService` | Publishes and restores in StartingAsync, opens the selected platform client in StartAsync, and closes it and stops lease renewal in StopAsync. |

An action may be an instance or a factory over the application's provider. CreateActions runs each
factory once, in order (Hosting D21); factory-created actions cannot be disposable, and disposable
collaborators belong in Services. Supplied action instances remain borrowed. The service itself
is owned by Database.Hosting and disposed once in the services phase, before engines. Its factory
is invoked directly, so the provider does not capture the service for disposal.

## Lifecycle

1. **Build provisions the engines.** Model factories open, recover and migrate declared databases
   inside DatabaseApplicationBuilder.Build. Inline work completes before Build returns; populated
   migrations may continue on engine-owned workers, with their existing admission gates.
2. **StartingAsync publishes and restores.** Once the host starts, every lifecycle service's
   StartingAsync completes before any service or server starts. The manifest repeats the deployment
   and exposes the added actions; the publication answer's snapshots are restored before serving.
   Publication or restore failure fails start. A lease is renewed until stop (Hosting D14).
3. **StartAsync opens the control plane.** Start order is AddService registrations, container
   IHostService registrations, the optional reopen service, then servers. The resource service
   opens the platform client after restore, before any Database server listens (Hosting D15/D19).
4. **Health and servers accept.** Hosting binds health to the runtime's http assignment when a
   gateway is named (T3-28), after the resource service opens and before model servers start.
   Each model server uses its named assignment (sql, documents, graph, kv or blob) only when its
   options carry no explicit listener. AddResource adds no wire server. The in-process platform
   currently assigns only http, so wire listeners still need explicit addresses there.
5. **Stop drains servers first.** The host reverses the complete order. Servers stop accepting and
   drain while the control plane remains open; then the reopen and container services stop. The
   resource service closes its client and stops renewing before AddService services stop.

Gateway StopAsync and Completion leave the Database application Started and accepting connections
(G37/G40). G42 DisposeAsync stops the gateway run, then disposes the owned platform. The platform
owns workload shutdown, so gateway disposal is the end-to-end test's shutdown criterion.

The gateway's startup window includes Build's open, recovery and inline schema migration, since
publication cannot begin until they finish. Database's host StartupTimeout bounds service start
only and supplies no construction timeout.

## Decisions

**D1 — One explicit integration.** Database.Hosting stays a plain host with no ApplicationModel
reference. This package is a public App.Database member; an Sdk.Database application calls
AddResource without another integration package reference. A gateway declares a Database resource
generically with `builder.AddResource("Database", "orders-db")`, without loading this area.

**D2 — The environment selects a platform.** The base options read platform and deployment through
Core's AppEnvironment, including each in-process program's own scope. UseInProcess and other
platform verbs opt into clients; no platform means no registration. No ambient instance registry
or generated manifest exists (Hosting D1/D19).

**D3 — No Database-owned actions yet.** The retired `database.add-database` and
`database.add-principal` commands do not fit the snapshot-restore model (ApplicationModel.Hosting
D3): a write is restored on every instance, and database data and principals live in each
instance's durable catalog. Only the application's added actions are exposed. Database management
actions need an owner design follow-up rather than replaying catalog mutations as snapshots.

**D4 — Health contributors use Hosting's observations (#1790, EN-A2).** The instance exposes one
IApplicationInstanceHealthContributor per engine and database, followed by database.accepting.
It resolves the application's reserved DatabaseApplicationContext singleton once. Each check maps
GetHealthAsync's transport-neutral observation directly to ApplicationInstanceHealthContribution,
including its description and immutable string data. No model type, private Web type, additional
health calculation, listener or platform registration is introduced by this mapping.

Contributor names are engine/<escaped-engine> and database/<escaped-engine>/<escaped-database>;
database names are uppercased invariantly before URI escaping, while engine identity stays ordinal.
Engine health reflects its state and workers independently of database health. An offline database
is Unhealthy, including while a pending reopen temporarily removes it from engine inventory.
Provisioning, held schemas and journal admission pauses are Degraded; held-schema failureCode and
detail retain the code, hashes and refused steps, within Hosting's existing 4,096-unit detail bound.
database.accepting is Healthy only at the existing outer-host Started gate, after all server starts
confirmed accept, and Unhealthy before start, during start/stop, after failure and after stop.

HealthContributors refreshes discovery on each property access so later opens, closes and pending
reopens remain represented. The current instance contract discovers contributors synchronously,
whereas Database inventory is asynchronous; a worker bridge avoids capturing the caller's
synchronization context. Each discovered contributor reads a fresh full observation pass when
checked, with the caller's cancellation token. A retained contributor whose database disappeared
reports Unhealthy until rediscovery removes it. There is no cache, subscription, timer or resource
ownership to tear down. Discovery and each check cost one inventory pass; checking all contributors
therefore repeats that pass. A future asynchronous batch health contract could avoid those repeats.

These contributions are complete without a consumer. The gateway still counts publication as
ready and consumes no instance health (ApplicationModel.Hosting open question 2; Gateway G18,
E-AM2). The contract has no readiness discriminator, so database.accepting names that contribution;
gateway aggregation and separate readiness remain ApplicationModel work. Unmanaged AddResource
still registers nothing, including no contributors or lifecycle service.

**D5 — Concrete-first and AOT.** DatabaseResourceOptions is a sealed public leaf with an internal
constructor; no new public interface or shipped friend grant exists. The internal resource carries
the borrowed context in a private field, filtered by its debugger proxy. Content
uses JsonApplicationContentSerializer.Create with exactly the added JSON contexts, with no
reflection fallback. The package raises no events and needs no EventSource.

## Open questions

1. **Named endpoint allocation — owner: ApplicationModel, E-AM1.** Database consumes sql, documents, graph, kv and blob runtime assignments when present. InProcessInfrastructure still assigns only http and one HTTP loopback endpoint output; that endpoint now serves Database health. Dependents continue passing wire addresses as literal settings until platforms allocate named endpoints and report per-endpoint outputs.
2. **Readiness — owner: ApplicationModel.Gateway (G18).** The gateway counts the first publication as ready, in StartingAsync before Database servers listen, so a dependent can deploy before Database accepts connections.
3. **Startup window — owner: gateway application.** Publication waits for Build's open, recovery and schema migration; the gateway's startup window must cover that work.
4. **Database-owned actions — owner: Database.** Define management operations that fit instance snapshot restore and each instance's durable catalog before adding actions.
5. **Instance health — owner: ApplicationModel.Hosting/Gateway, E-AM2.** Database now contributes health. Define gateway consumption, a readiness discriminator and asynchronous batch discovery/evaluation (Hosting open question 2; Gateway G18) without delaying these valid instance contributions.
6. **Publication-to-open window — owner: ApplicationModel.Hosting/Database.** AddService services always start after publication and restore but before the control plane opens. A slow service widens Hosting open question 7's window, and Database users cannot reorder these services as Web and EmailHub users can.
7. **Read-only actions — owner: Database, T3 EN-A3.** HTTP health and instance contributors now share Hosting's observations. Read-only databases and health actions remain a separate run; this integration still exposes only application-added actions.
