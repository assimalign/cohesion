# Assimalign.Cohesion.Hosting.Resources

## Summary

Provides Cohesion's opt-in runtime context and supervisor boundary for enabled resource
executables. It owns the transport-neutral health contracts and composes the plain Hosting run seam;
plain Hosting does not depend back on Resources.

## Dependencies

| Dependency | Use |
| --- | --- |
| `Assimalign.Cohesion.Core` | Environment contract (`AppEnvironment`, `AppEnvironment.Variables`), paths, URI helpers, and common primitives |
| `Assimalign.Cohesion.Hosting` | Host lifecycle and `IHostRunner`/`IHostRunObserver` seam |
| `System.Security.Cryptography.ProtectedData` | Windows CurrentUser DPAPI mount payloads |

The base `Assimalign.Cohesion.App` framework delivers Resources because every opt-in resource SDK
generates `ResourceRuntime` calls. Assembly presence does not activate this runtime; the generated registration exists only
when `CohesionApplicationModel=enabled`. Direct package consumers can still select the packages
they need.

## Primary responsibilities

- `ResourceContext` snapshots identity, environment, content root, endpoints, mounts, settings,
  references, the application trust key, bootstrap credential, and additional frozen contract
  values for one invocation.
- `ResourceRuntime` supplies AsyncLocal scope, assembly-keyed generated registrations, entry
  invocation, and host surrender.
- `IResourceControlPlane` owns host-local contributors, endpoint observations, accepted command
  kinds, health aggregation, command dispatch, and the runtime surface: configuration sections,
  actions and tools, frozen when the host is built, and the deployment record applied over them.
- `ResourceMount` reads file-backed or in-memory mount values, decrypting Windows DPAPI-backed
  file content for consumers.
- The internal resource runner handles process signals, readiness protocol lines, shutdown budget,
  content-root validation, and stable exit classification.

## Key types

- `ResourceContext`, `ResourceMount`, `ResourceRuntime`, `ResourceEntryExitException`
- `IResourceEntryInvocation`
- `IResourceControlPlane`, `ResourceControlPlane` (public sealed implementation; create with `ResourceControlPlane.Create(kinds)`)
- `ResourceCommand`
- `ResourceConfigurationSection`, `ResourceAction`, `ResourceTool`, `ResourceRuntimeSurface`
- `ResourceConfigurationRecord`, `ResourceConfigurationStatus`, `ResourceConfigurationSectionStatus`
  and the `ResourceConfigurationPath`, `ResourceActionName`, `ResourceToolName` value objects
- `IResourceCredentialVerifier`, `ResourceCredentialPresentation`, `ResourceCredentialVerification`,
  `ResourceCredentialStatus`, `ResourceCaller`, `ResourceCallerKind`, `ResourceCredentialProfile`

Health value types and `IHealthContributor` are owned by this assembly under its
`Assimalign.Cohesion.Hosting.Resources` namespace. Host lifecycle types are owned by
`Assimalign.Cohesion.Hosting`. There is no separate Health package or compatibility shim.

## Health contracts

`HealthStatus` is ordered `Unhealthy = 0`, `Degraded = 1`, `Healthy = 2`. Default values fail closed.
`HealthContribution` carries status, an optional description and optional diagnostic data; its
Healthy/Degraded/Unhealthy factories preserve those fields. `IHealthContributor` exposes a stable
`Name` and cancellable `ValueTask<HealthContribution> CheckAsync`. `ResourceHealthCheck` is the
corresponding delegate and `ResourceHealthReport` carries the aggregate status and named snapshots.
Contributor registration and aggregation live in the same Resources package as these contracts.

## Invocation model

Generated module initializers register the resource assembly's compiler-rooted entry point and an
generic control-plane factory. Each builder obtains a new control plane. When the area builder
calls `ResourceRuntime.HostBuilt`, the runtime observes the invocation endpoints, attaches the host,
installs the resource runner, and completes `IResourceEntryInvocation.HostReady` when applicable.

`ResourceRuntime.InvokeEntry` requires an explicit `CreateScope` frame. It invokes the registered
`Assembly.EntryPoint` on a dedicated invocation thread and returns separate `HostReady` and
`Completion` tasks. This is the one sanctioned reflection operation; there is no assembly scan or
runtime entry-type lookup.

A caller with an independently validated, compiler-rooted executable assembly can query
`ResourceRuntime.IsEntryRegistered` and use `ResourceRuntime.InvokeEntryPoint` as a direct fallback
when generated entry registration is unavailable. The fallback does not weaken `InvokeEntry`'s
registration guard and retains the same explicit-scope, single-invocation, dedicated-thread, and
completion semantics.

## Process protocol

For an ordinary resource process whose started hook completes before a stop is accepted, stdout
transitions are:

1. `cohesion-resource: ready` after the Hosting observer reports `Started`.
2. `cohesion-resource: stopping` when the stop transition is accepted.
3. `cohesion-resource: stopped` after the host finishes its stop sequence.

The host can accept a stop while `OnStartedAsync` is still running. In that race, `ready` is omitted,
the protocol begins with `stopping`, and no late `ready` is emitted.

A failed `ready` write requests shutdown so the host still drains. A failure writing any protocol
line is retained and classified at the executable boundary; `stopping` and `stopped` failures do not
request a shutdown already in progress. In-process entry invocation suppresses stdout protocol,
process-signal subscriptions, named-event waits, and `Environment.ExitCode`; failures instead fault
`IResourceEntryInvocation.Completion` with `ResourceEntryExitException` carrying the classified
exit code.

## Exit mapping

| Code | Meaning |
| ---: | --- |
| 0 | Successful run and drain |
| 64 | Area-classified configuration failure |
| 69 | Area-classified dependency failure |
| 70 | Unclassified failure before ready |
| 75 | Unclassified failure after ready |
| 130 | Drain budget cancelled after SIGINT or the Windows SIGQUIT/CTRL_BREAK mapping |
| 143 | Drain budget cancelled after termination, SIGHUP, named stop event, or another stop request |

Typed area classification checks the currently inspected exception first, so a directly typed outer
exception wins. When that exception is unclassified, classification recurses through its inner
exception or aggregate members; configuration wins over dependency among recursively inspected
aggregate members. Any typed result takes precedence over generic phase/drain classification.


## Declarative commands

Runtime packages register an IResourceCommandHandler for each implemented advertised kind through
IResourceControlPlane.RegisterCommandHandler. Dispatch rejects blank identity fields and unsupported
kinds before invoking runtime mutations. A serialized per-plane ledger caches identical owner/id
replays, refuses another owner's claim to the same key, and prevents stale declarations from deleting
a newer declaration. Successful deletion releases the key. Commands exposes an owner-bearing snapshot;
the HTTP adapter deliberately excludes payloads from listings.

## Resource credentials

The default resource credential is the ES256 application-key JWT, which every area hosting module
verifies without registration. An application whose gateway issues another kind of credential
registers an `IResourceCredentialVerifier` factory with
`ResourceRuntime.RegisterCredentialVerifier(assembly, factory)`. Area modules resolve the invocation's
verifier with `ResourceRuntime.TryGetCredentialVerifier(context)`, consult it first, fall through to
the application-key verification on `NoResult`, and authorize the mapped `ResourceCaller`. The
default profile's claim, scope, audience, clock-skew, and lifetime constants are
`ResourceCredentialProfile`, which lives here beside the verifier seam because every gateway and
hosting module that mints or verifies the default credential already references this package. The
types are BCL-only, so the guarded ApplicationModel closure does not grow. See
[DESIGN.md](DESIGN.md#resource-credential-verification).

Standalone HTTP control planes (no gateway identity) accept `/cohesion/v1` and its descendants
only from transport peers at `127.0.0.1`, `::1`, or IPv4-mapped `127.0.0.1`; other or unknown peers
receive 403. Forwarded headers cannot grant access. Bare `/healthz`, `/readyz`, and `/livez` probes
stay public. Gateway-managed credential checks are unchanged. This restricts remote standalone
management compared with 10.0.0-preview.1 (#1431); use loopback for standalone development and a
gateway identity with credentials for remote management.

## Endpoint certificate contract (31t)

`ResourceContext.TryGetEndpointCertificate(endpoint, out leaf, out chain)` reads one ordinary Secret mount. The full constructor accepts an optional endpoint-to-mount dictionary; generated Resource.g.cs registers the same immutable metadata by executable assembly through ResourceRuntime.RegisterEndpointCertificates. Control-plane creation applies it to the current invocation. Without a mapping, the conventional mount is `tls`; a normalized environment lookup also handles names containing punctuation. The explicit-mount overload supports manually configured Web endpoints.

Absent or empty mounts return false. Present material requires the leaf as the first certificate and exactly one private key anywhere, accepting PKCS#8, EC, and RSA key labels. Chain parsing accepts either producer order and includes supplied roots. PKCS#12 re-import gives Windows SslStream a usable key association. The caller owns and disposes the returned leaf and chain. Intermediate secret buffers are cleared. The implementation uses only BCL cryptography and preserves the COHAM001 closure and the existing ResourceMount carrier.

`TryGetTrustBundle` reads AppEnvironment.Variables.TrustBundlePath through ResourceMount's protected-file reader. `CreateOutboundTrustValidator` preserves missing-certificate and hostname rejection, then builds a server-authentication chain with CustomRootTrust, CustomTrustStore and NoCheck revocation against the supplied anchors. It does not disable TLS validation.
