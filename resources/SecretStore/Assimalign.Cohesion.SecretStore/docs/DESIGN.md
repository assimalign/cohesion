# Assimalign.Cohesion.SecretStore Design

## Design intent

The area root owns the contracts that executable composition and feature packages target.
`ISecretStoreApplicationBuilder` is the contract-only builder seam, while
`ISecretStoreApplication` supplies the hosting-free application lifecycle. The
root also owns the two foundational SecretStore declarations needed by a `Program.cs`:
`AddSecret` and `AddCertificateAuthority`. They are builder members rather than Hosting-owned
verbs, so any implementation of the root seam can consume the same declaration data.

## Hosting isolation

The root contracts are hosting-free (O34): `ISecretStoreApplication` exposes `Context`, `StartAsync`, and `StopAsync`; `ISecretStoreApplicationContext` exposes `ContentRootPath`. `ISecretStoreApplicationBuilder` owns area declarations and `Build()`. The root and feature packages reference no `Assimalign.Cohesion.Hosting*` library; COHRES004 enforces the boundary.

The concrete application, builder, and context live in `Assimalign.Cohesion.SecretStore.Hosting`; options and supporting services remain internal.

`CertificateAuthorityOptions` is composition data: it introduces no network, certificate,
configuration, or service-container dependency into the root.

## Code-first secret seeds

`AddSecret(path, value)` declares first-start seed data. An implementation snapshots the bytes at
registration and writes them only when the durable store has no version at that exact, ordinal
path. This distinction is deliberate: composition must not roll back a value later changed by
rotation or a command. Duplicate declarations are rejected instead of making call order an
implicit overwrite policy.

The path parameter is the generated `SecretStoreSeedPath`. Its `IsValid` owns the seed rule:
nonblank text excluding exact `trusted-issuers.json`. The builder rejects `default`, and the
runtime validates seed registrations through the same predicate before persistence. Logical
seed paths are not file-system paths; they retain separators, spaces around nonblank text, and
the `certs/` prefix. The seed protocol and durable repository use ordinal identity, so the
generated value object uses `ObjectComparison="Ordinal"` and never normalizes spelling.
Command write paths have a separate rule that reserves `certs/` as well.

Replacing the `string` parameter on `ISecretStoreApplicationBuilder.AddSecret` with
`SecretStoreSeedPath` is a source and binary compatibility change from `10.0.0-preview.1` for
implementations and bound method signatures. Ordinary string arguments remain supported through
the generated checked conversion. The seed acceptance grammar is unchanged.

## Certificate-authority bootstrap

`AddCertificateAuthority` captures one `CertificateAuthorityOptions` declaration. Existing durable
authority state always wins. On first start, the decision order is:

1. Use the paired `InitialCertificate` and `InitialPrivateKey` PEM material when supplied. This is
   the explicit `parameter:`/operator-seeded route.
2. Otherwise, when `PlatformEnrollmentEndpoint` is configured, create durable pending state for
   gateway-mediated enrollment with the Platform SecretStore. `PlatformCertificate` optionally
   pins the Platform trust anchor when the response is completed.
3. Otherwise, when `SelfSeedWhenNoPlatform` is true (the default), generate a self-signed
   development root.
4. Otherwise, fail configuration because no authority source exists.

A configured or incomplete Platform enrollment never falls through to self-seeding. Silent fallback
would split the application's certificate hierarchy from the Platform hierarchy and hide an
operational trust failure. The default common name is stable for standalone use and can be
overridden for an application-specific root. Intermediate requests use the authenticated ambient
application/resource identity as their subject.

## Composition lifecycle

Background-work registration belongs to the concrete `SecretStoreApplicationBuilder`: `AddService(IHostService)` and `AddService(Func<SecretStoreApplicationContext, IHostService>)`. The factory receives the same concrete context as Web's and Database's AddService, so hosting consumers can use environment, state, and hosted-service members beyond the small root contract. Database's root-level AddServer keeps the interface context. Factories run once per build against the same context retained by the application; the hosted-service snapshot is installed after factory evaluation. Services start in registration order and stop in reverse. No area-owned service abstraction is introduced.

Application defaults are described by the Hosting design. A managed resource is opted into by the application with `builder.Services.AddResource(...)` from `Assimalign.Cohesion.SecretStore.Hosting.ApplicationModel`.

## AOT posture

The contracts and option values require no reflection, dynamic code generation, runtime assembly
scanning, configuration binding, or container-based activation. Secret and certificate material is
carried as `ReadOnlyMemory<byte>`, leaving parsing and cryptographic ownership to the Hosting
implementation while the public root remains trimming- and NativeAOT-safe.

## Non-goals

This project does not persist or encrypt secrets, generate or enroll certificates, contact the
Platform service, verify bootstrap credentials, or expose a protocol endpoint. Those are runtime
responsibilities. Seed declarations are builder-time only; changing a running store is the
resource integration's, through the actions its application adds.

## Hosting-free application contract (O34)

`SecretStoreApplication.CreateBuilder(args)` returns the public concrete `SecretStoreApplicationBuilder`; its `Build()` returns the public `SecretStoreApplication : Host<SecretStoreApplicationContext>`. The public `SecretStoreApplicationContext` implements `ISecretStoreApplicationContext`, reading `ContentRootPath` from the host environment. The application explicitly forwards the root lifecycle contract to `IHost`, and consumers use the concrete application for `RunAsync` and `await using`. Runtime options and supporting services remain internal.

Background-work registration (`AddService`) is available only on the concrete Hosting builder.
