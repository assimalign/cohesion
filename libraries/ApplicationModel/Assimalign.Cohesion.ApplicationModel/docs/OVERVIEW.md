# ApplicationModel

This Core-only package declares resources, dependency edges, external references, portable
realization plans, and application-owned resource commands. It supplies `IApplicationBuilder`,
`IApplicationModel`, application sets, and the transport-neutral gateway contracts.

`IApplicationEnvironment.IsLocal` identifies developer-machine execution; `IsDevelopment`
identifies the strict deployed Development environment. Local and InProcess gateways default
to Local only when both command-line and process-environment input omit a nonblank environment.
Explicit values remain authoritative, and other gateways retain the Core resolution rule.

```csharp
var builder = Application.CreateBuilder(ApplicationName.Parse("orders"), args)
    .UseGateway(gateway);
var database = builder.AddDatabase(databaseManifest);
database.AddDatabase("orders", engine: "sql");
var api = builder.AddWeb(apiManifest).DependsOn(database);
await builder.Build().RunAsync();
```

`AddDatabase`, `AddWeb`, and their typed descriptor command verbs come from their area's
ApplicationModel packages. A command is recorded as desired state. `Build()` validates its kind
against the target manifest and its owner and target against the declaring graph. The gateway
applies it after the target reaches Running and before admitting dependents.
Each target ownership key permits one desired command; competing values or set/remove
declarations are rejected. A subsequent model can replace the desired operation.

Custom area packages extend `IResourceCommandDescriptor` and pass their source-generated
`JsonTypeInfo<T>` to `AddCommand`. Explicit declarations use `ResourceCommands.Create` and
`IApplicationBuilder.AddCommand`. All implementations stay internal. A typed wrapper retains
its underlying resource reference; names alone never establish graph membership.

Built models expose immutable `Commands`. Portable model documents retain canonical command
payloads for application-set composition, so command payloads must never contain secrets.
Configuration-store verbs carry ordinary configuration values; SecretStore's `AddSecret` carries only
a `parameter:<name>` or `<store>:<key>` source, which the gateway resolves at delivery.

A target's manifest can mark a command kind `ResourceManifestCommand.RequiresInputResolver`
(`resource.json` then holds `{ "kind": "...", "requiresInputResolver": true }` instead of the plain
kind string); SecretStore marks `secretstore.add-secret`. `Build()` fails when the declaring
application declares such a command without an `IResourceCommandInputResolver` for its kind (see
below). An unflagged kind with no resolver is delivered as declared.

See [DESIGN.md](DESIGN.md) for identity, validation, ownership, external references, lifecycle,
and NativeAOT boundaries; see the [package README](../README.md) for model and set composition.

## Provider registrations

This package also defines the seams a gateway uses to reach resource-area behaviour it must not
reference: `IResourceSourceProvider` (mount sources), `IResourceCertificateAuthority`,
`ITrustedIssuerStore`, `IResourceCommandInputResolver`, `ResourceTelemetrySink`,
`IApplicationCredentialIssuer`, and `IApplicationCallerAuthenticator`, plus `TrustedIssuer`. The
library defines them and the gateway calls them; opt-in
`Assimalign.Cohesion.<Area>.ApplicationModel.Orchestration` packages under `resources/` implement
them. Nothing is registered by convention — a gateway `Program.cs` references the package and
calls its verb, which fills `builder.Providers`:

```csharp
IApplicationResourceDescriptor secrets = builder.AddResource(Manifests.Secrets);
builder.UseSecretStore(secrets).AsCertificateAuthority().AsTrustStore();
builder.Providers.Sources["vault"] = new VaultSourceProvider(); // a hand-written provider
```

`Build()` copies `builder.Providers` into `IApplicationModel.Providers` as a frozen snapshot and
validates it: every `<source>:<key>` mount source of the application needs a provider, bound
resources must exist with the right kind, no source or binding may reach into another
application (cross-application store sources are not supported yet), and every command whose
target manifest requires an input resolver needs one registered for its kind — for SecretStore's
`secretstore.add-secret`, the resolver `UseSecretStore(...)` registers. A failure is an
`InvalidOperationException` that names the package and verb to reference. The store still rejects
an unresolved `secretstore.add-secret` at delivery, as defense in depth.
`ApplicationProviders.Empty` is what a model without registrations, or one imported from a document,
carries; imported models never inherit another model's registrations.

An application set hands an `IApplicationProviderBuilder` to each member it adds with a registration
callback, because a member's model comes from its describe output and carries no providers. It is a
separate interface from `IApplicationBuilder` (the default builder implements both). The orchestration
verbs take a resource name on that surface, where a builder's verbs take the descriptors it created;
both forms write the same `ApplicationProviders`:

```csharp
IApplicationSet set = Application.CreateSet(new LocalGateway(options), args)
    .AddApplication(Applications.Platform, platform => platform
        .UseSecretStore("platform-secrets")
        .AsCertificateAuthority()
        .AsTrustStore())
    .AddApplication(Applications.AppA, appa => appa.UseConfigurationStore("appa-configuration"))
    .AddApplication(Applications.AppB);
```

The set freezes each member's registrations, attaches them to that member's model alone, and
validates every member with the `Build()` rules before contacting the gateway; errors name the member
application. See [Provider seams](DESIGN.md#provider-seams-explicit-registration) for the rules and
the defaults when nothing is registered.

The identity seams are live in the shipped gateway: every credential it mints (tagged by
`ApplicationCredentialPurpose`) goes through `Providers.CredentialIssuer` before the default ES256
application-key issuer, and its control plane runs `Providers.Callers` after the built-in
trusted-issuer authenticator. An application whose resources receive credentials from another
issuer registers a matching `IResourceCredentialVerifier` on them through
`ResourceRuntime.RegisterCredentialVerifier` (`Assimalign.Cohesion.Hosting.Resources`).

## Trust grant command options

GatewayCommand carries an immutable AllowedCommandKinds list. Repeatable, comma-separated
--allow options are valid only in trust-add mode. Absent or empty grants mean unrestricted kinds;
trust-issue rejects the option. ApplicationModel parses and carries policy; the serving gateway
enforces it on apply and delete. The CLI's --against option remains deferred.
