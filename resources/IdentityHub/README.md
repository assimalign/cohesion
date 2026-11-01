# IdentityHub

IdentityHub is the L3 identity service platform intended to manage tenants, directories, applications, principals, credentials, sessions, token services, federation, and provisioning.

The application builder configures a minimal code-first OpenID Connect issuer with persisted ES256 keys, client credentials, and a loopback Local device-authorization flow. An IdentityHub application becomes a managed resource instance with `Assimalign.Cohesion.IdentityHub.Hosting.ApplicationModel`: `builder.Services.AddResource("identity", resource => resource.UseInProcess())` joins the gateway that runs it through the platform it opted into. The self-signed fallback is Local-only.

## Project map

An arrow means "references": `IdentityHub.Hosting --> IdentityHub` reads
`IdentityHub.Hosting` references `Assimalign.Cohesion.IdentityHub`.

```mermaid
flowchart LR
    P0["IdentityHub — area root"]
    P3["IdentityHub.Hosting — runtime module"]
    PAM["IdentityHub.Hosting.ApplicationModel — resource integration"]
    AMH["Assimalign.Cohesion.ApplicationModel.Hosting — L2"]
    P4["IdentityHub.Models"]
    CORE["Assimalign.Cohesion.Core — L1"]
    HOSTFAM["Assimalign.Cohesion.Hosting family — L2"]
    PRIV["other areas, referenced privately"]
    P0 --> CORE
    P3 --> HOSTFAM
    P3 --> P0
    P3 -->|"private"| PRIV
    P4 --> CORE
    PAM --> P3
    PAM --> P0
    PAM --> AMH
    AMH --> HOSTFAM
    P0 -.->|"COHRES001 ✗"| P3
```

Solid edges are the references this area permits. The dotted edge is the one `COHRES001`
rejects: **no library in the area may reference its own `IdentityHub.Hosting` runtime
module**. The area root and its feature
libraries likewise reference no `Assimalign.Cohesion.Hosting*` library at all (`COHRES004`).

The one exception is the resource integration, `IdentityHub.Hosting.ApplicationModel`, which joins
the runtime module to the application model's hosting; `COHRES001` sanctions that
reference by the integration's exact name, with no exemption property.

The full reference graph for every Cohesion assembly, including the exact external dependencies
collapsed above, is in [docs/DEPENDENCIES.md](../../docs/DEPENDENCIES.md).

## Projects

- `Assimalign.Cohesion.IdentityHub` defines the public area-root application and builder contracts alongside the existing identity domain contracts.
- `Assimalign.Cohesion.IdentityHub.Hosting` provides the concrete creation entry point, issuer endpoints, key persistence, and host lifecycle.
- `Assimalign.Cohesion.IdentityHub.Hosting.ApplicationModel` registers the application as a managed resource instance (`builder.Services.AddResource(name, ...)`) on the platform its gateway names, with the actions the application adds.
- `Assimalign.Cohesion.IdentityHub.Models` retains the existing tenant-directory DTO shapes while linking principals and credentials to the canonical `Assimalign.Cohesion.IdentityModel` contracts.

## Layering and dependencies

As an L3 service platform, IdentityHub composes the L2 `Assimalign.Cohesion.Hosting` runtime, which is built on the L1 `Assimalign.Cohesion.Core` foundation. The root and Models projects consume the L1 `Assimalign.Cohesion.IdentityModel` contracts; Models also retains Core for its generated ULID identifiers. The Hosting project publicly depends only on the area root, DependencyInjection, and the shared Hosting runtime; its Web, HTTP, Connections, and IdentityModel references are private.

Applications declare audiences and clients in code with `AddAudience` and `AddClient` on `IIdentityHubApplicationBuilder`.

## Project documentation

- [Root overview](./Assimalign.Cohesion.IdentityHub/docs/OVERVIEW.md)
- [Root design](./Assimalign.Cohesion.IdentityHub/docs/DESIGN.md)
- [Hosting overview](./Assimalign.Cohesion.IdentityHub.Hosting/docs/OVERVIEW.md)
- [Hosting design](./Assimalign.Cohesion.IdentityHub.Hosting/docs/DESIGN.md)
- [Resource integration overview](./Assimalign.Cohesion.IdentityHub.Hosting.ApplicationModel/docs/OVERVIEW.md)
- [Resource integration design](./Assimalign.Cohesion.IdentityHub.Hosting.ApplicationModel/docs/DESIGN.md)
- [Models overview](./Assimalign.Cohesion.IdentityHub.Models/docs/OVERVIEW.md)
- [Models design](./Assimalign.Cohesion.IdentityHub.Models/docs/DESIGN.md)

## Application composition (O34)

The root contracts are hosting-free (O34): `IIdentityHubApplication` exposes `Context`, `StartAsync`, and `StopAsync`; `IIdentityHubApplicationContext` exposes `ContentRootPath`. `IIdentityHubApplicationBuilder` owns area declarations and `Build()`. The root and feature packages reference no `Assimalign.Cohesion.Hosting*` library; COHRES004 enforces the boundary.

`IdentityHubApplication.CreateBuilder(args)` returns the public concrete `IdentityHubApplicationBuilder`; its `Build()` returns the public `IdentityHubApplication : Host<IdentityHubApplicationContext>`. The public `IdentityHubApplicationContext` implements `IIdentityHubApplicationContext`, reading `ContentRootPath` from the host environment. The application explicitly forwards the root lifecycle contract to `IHost`, and consumers use the concrete application for `RunAsync` and `await using`. Runtime options and supporting services remain internal.

```csharp
using Assimalign.Cohesion.IdentityHub.Hosting;

IdentityHubApplicationBuilder builder = IdentityHubApplication.CreateBuilder(args);
// Add area declarations and optional hosting services before Build().
await using IdentityHubApplication application = builder.Build();
await application.RunAsync();
```
