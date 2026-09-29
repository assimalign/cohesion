# SecretStore

SecretStore is the L3 operational service platform for protected secret persistence, application trust, and private certificate workflows. The substantive runtime stores protected files on its persistent `data` volume, verifies gateway-issued ES256 bootstrap credentials, and issues durable private-CA leaves on first resolution of `certs/<name>`.

## Project map

An arrow means "references": `SecretStore.Hosting --> SecretStore` reads
`SecretStore.Hosting` references `Assimalign.Cohesion.SecretStore`.

```mermaid
flowchart LR
    P0["SecretStore — area root"]
    P1["SecretStore.ApplicationModel"]
    P2["SecretStore.Client"]
    P3["SecretStore.Hosting — runtime module"]
    P4["SecretStore.ApplicationModel.Orchestration — opt-in gateway providers"]
    CORE["Assimalign.Cohesion.Core — L1"]
    HOSTFAM["Assimalign.Cohesion.Hosting family — L2"]
    APPMODEL["Assimalign.Cohesion.ApplicationModel — L2"]
    PRIV["other areas, referenced privately"]
    P0 --> CORE
    P1 --> APPMODEL
    P1 --> HOSTFAM
    P2 --> CORE
    P3 --> HOSTFAM
    P3 --> P0
    P3 -->|"private"| PRIV
    P4 --> APPMODEL
    P4 --> P2
    P1 -.->|"COHRES001 ✗"| P3
    P4 -.->|"COHRES004 ✗"| HOSTFAM
```

Solid edges are the references this area permits. Dotted edges are references the build
rejects. The `COHRES001` edge is the rule for the whole area: **no library in the area may
reference its own `SecretStore.Hosting` runtime module**, and the declarative `.ApplicationModel` package in particular never does — generated
code in an opted-in consumer executable joins the two sides at run time through
`Assimalign.Cohesion.Hosting.Resources.ResourceRuntime` instead. The area root and its feature
libraries likewise reference no `Assimalign.Cohesion.Hosting*` library at all (`COHRES004`); the
dotted `COHRES004` edge marks that rule for the `.Orchestration` package, which references only
`Assimalign.Cohesion.ApplicationModel` and the client.

The full reference graph for every Cohesion assembly, including the exact external dependencies
collapsed above, is in [docs/DEPENDENCIES.md](../../docs/DEPENDENCIES.md).

## Projects

- `Assimalign.Cohesion.SecretStore` defines the public area-root application and builder contracts alongside the existing secret-store abstraction.
- `Assimalign.Cohesion.SecretStore.Hosting` provides the concrete creation entry point, protected file store, trust verifier, private CA, and `/cohesion/v1` HTTP protocol host.
- `Assimalign.Cohesion.SecretStore.Client` is the thin, Core-only HTTP protocol client that the opt-in orchestration package uses on a gateway's behalf to read secret bytes and PEM certificates and to post trust grants. A gateway never references it directly.
- `Assimalign.Cohesion.SecretStore.ApplicationModel` owns the typed resource descriptor, single-replica StatefulSet planner, and default resource control plane injected by `Sdk.SecretStore`.
- `Assimalign.Cohesion.SecretStore.ApplicationModel.Orchestration` is the opt-in gateway package: `builder.UseSecretStore(store)` registers the store as the source of `<store>:<key>` secret and certificate mounts and resolves `secretstore.add-secret` sources at delivery, and `.AsCertificateAuthority()` / `.AsTrustStore()` make it the application's certificate authority and trusted-issuer store. It references only `Assimalign.Cohesion.ApplicationModel` and the client, and is never injected: a gateway references it and registers it explicitly.

## Layering and dependencies

As an L3 service platform, SecretStore composes the L2 Hosting runtime rather than defining its own host lifecycle. Only `SecretStore.Hosting` crosses into the Web, IdentityModel, and Security implementations needed to serve HTTP/TLS, verify ES256 credentials, and protect durable files. The area root remains free of those dependencies.

The client is Core-only and never references `*.Hosting` or an ApplicationModel assembly, and it is not delivered through the `App.SecretStore` shared framework. The gateway library references no client: mount-source resolution, certificate issuance, and trust persistence reach a SecretStore through the opt-in orchestration package below, which uses the client on the gateway's behalf, and command delivery uses the gateway's generic control-plane client. Platform gateways continue to avoid SecretStore ApplicationModel packages. `Sdk.SecretStore` injects that NuGet-only package into enabled resource applications; it is not part of the `App.SecretStore` reference framework.

The SecretStore wire knowledge a gateway needs — secret and certificate reads, `ca/root` anchors, `certs/<leaf>` issuance, `trusted-issuers.json`, `cohesion.trust.add`, and `secretstore.add-secret` payload resolution — lives in the NuGet-only `SecretStore.ApplicationModel.Orchestration` package as implementations of the `Assimalign.Cohesion.ApplicationModel` provider seams, not in the gateway library. A gateway uses a SecretStore only through the providers its `Program.cs` registers: `Build()` rejects a `<store>:<key>` mount whose store has no registration, and a declared `AddSecret` when no `UseSecretStore(...)` registers its input resolver (the manifest marks `secretstore.add-secret` `requiresInputResolver`); outside Local, an endpoint certificate without a source needs `.AsCertificateAuthority()` and `cohesion trust add` needs `.AsTrustStore()` (Local alone falls back to the gateway development authority and the `.cohesion/<app>/trust/trusted-issuers.json` file).

## Certificate scope

- A store with no Platform authority creates a self-signed development root on first start.
- `certs/<name>` creates a persistent private leaf, renews it near expiration, and returns a PEM bundle containing the leaf, its PKCS#8 key, and its issuer chain.
- The host exposes request, Platform-signing, and completion operations for gateway-mediated intermediate enrollment. Automatic first-start forwarding still requires the gateway-owned signing/target-audience seam described in the Hosting design.
- A `parameter:` certificate source is resolved and mounted by the gateway without alteration; the SecretStore protocol is not called for that source.
- `Certificate="public"` and ACME/public-CA issuance are intentionally deferred to a later design item.

## Project documentation

- [Root overview](./Assimalign.Cohesion.SecretStore/docs/OVERVIEW.md)
- [Root design](./Assimalign.Cohesion.SecretStore/docs/DESIGN.md)
- [Hosting overview](./Assimalign.Cohesion.SecretStore.Hosting/docs/OVERVIEW.md)
- [Hosting design](./Assimalign.Cohesion.SecretStore.Hosting/docs/DESIGN.md)
- [Client overview](./Assimalign.Cohesion.SecretStore.Client/docs/OVERVIEW.md)
- [Client design](./Assimalign.Cohesion.SecretStore.Client/docs/DESIGN.md)
- [ApplicationModel overview](./Assimalign.Cohesion.SecretStore.ApplicationModel/docs/OVERVIEW.md)
- [ApplicationModel design](./Assimalign.Cohesion.SecretStore.ApplicationModel/docs/DESIGN.md)
- [Orchestration overview](./Assimalign.Cohesion.SecretStore.ApplicationModel.Orchestration/docs/OVERVIEW.md)
- [Orchestration design](./Assimalign.Cohesion.SecretStore.ApplicationModel.Orchestration/docs/DESIGN.md)

## Declarative control-plane commands

| Wire kind | Descriptor verb | Ownership key |
|---|---|---|
| `secretstore.add-secret` | `AddSecret` | secret path |
| `secretstore.issue-certificate` | `IssueCertificate` | certificate name |

The [ApplicationModel](Assimalign.Cohesion.SecretStore.ApplicationModel/docs/OVERVIEW.md) declares
commands; [Hosting](Assimalign.Cohesion.SecretStore.Hosting/docs/DESIGN.md) applies them; the gateway's
generic `ResourceControlPlaneCommandClient` (`Assimalign.Cohesion.ApplicationModel.Gateway`) delivers them
to the resource control plane. Before delivery, the `SecretStoreAddSecretInputResolver` that
`UseSecretStore(store)` registers resolves an `AddSecret` source into the payload. ApplicationModel,
Client, and ApplicationModel.Orchestration are standalone NuGet packages.

## Application composition (O34)

The root contracts are hosting-free (O34): `ISecretStoreApplication` exposes `Context`, `StartAsync`, and `StopAsync`; `ISecretStoreApplicationContext` exposes `ContentRootPath`. `ISecretStoreApplicationBuilder` owns area declarations and `Build()`. The root and feature packages reference no `Assimalign.Cohesion.Hosting*` library; COHRES004 enforces the boundary.

`SecretStoreApplication.CreateBuilder(args)` returns the public concrete `SecretStoreApplicationBuilder`; its `Build()` returns the public `SecretStoreApplication : Host<SecretStoreApplicationContext>`. The public `SecretStoreApplicationContext` implements `ISecretStoreApplicationContext`, reading `ContentRootPath` from the host environment. The application explicitly forwards the root lifecycle contract to `IHost`, and consumers use the concrete application for `RunAsync` and `await using`. Runtime options and supporting services remain internal.

```csharp
using Assimalign.Cohesion.SecretStore.Hosting;

SecretStoreApplicationBuilder builder = SecretStoreApplication.CreateBuilder(args);
// Add area declarations and optional hosting services before Build().
await using SecretStoreApplication application = builder.Build();
await application.RunAsync();
```
