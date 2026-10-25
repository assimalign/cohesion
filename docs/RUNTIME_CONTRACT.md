# Cohesion Runtime Contract v1

This document is the language-neutral contract between a Cohesion gateway and a resource.
The contract is frozen at version 1: changing a variable name or its meaning is a breaking
wire change. .NET consumers take the names from `AppEnvironment.Variables` in Core (namespace
`Assimalign.Cohesion`), the contract's only .NET home for every variable below and the name
builders, and read values through `AppEnvironment`'s readers (`GetValue`, `TryGetPort`,
`TryGetUri`, `TryGetEndpoint`, `TryGetDependency`, `TryGetMount`); non-.NET workloads use this
table directly. The typed .NET endpoint readers return `System.Uri`, and
Core's `System.UriExtensions` supplies endpoint validation, construction, parsing, and canonical
formatting without introducing a Cohesion-specific address type. That .NET binding was amended in
place on 2026-09-26 (R9 in the [developer-experience design](DEVELOPER_EXPERIENCE_DESIGN.md)); no
variable name, value, or wire behavior changed.

Out-of-process resources receive values through process environment variables. In-process
resources receive the same keys through the ambient resource context introduced by runtime
contract item 12. A standalone process with no gateway has `COHESION_GATEWAY` unset and resolves
its environment name as `COHESION_ENVIRONMENT ?? DOTNET_ENVIRONMENT ?? "Production"`.

## Variable contract

| Variable | Value shape | When set | Carrier by topology |
| --- | --- | --- | --- |
| `COHESION_APPLICATION` | Application name | Every gateway-realized Cohesion resource | Local: environment; In-process: ambient context; Docker: environment; Kubernetes: ConfigMap |
| `COHESION_RESOURCE` | Resource name | Every gateway-realized Cohesion resource | Local: environment; In-process: ambient context; Docker: environment; Kubernetes: ConfigMap |
| `COHESION_RESOURCE_INSTANCE` | Platform instance identity | Platforms that expose individual instances | Kubernetes: Downward API `metadata.uid`; the resource reports it with its configuration status |
| `COHESION_CONFIG_DIRECTORY` | Absolute directory path holding the resource's deployment record (see [Deployment record](#deployment-record)) | When the gateway delivers a deployment record | Local/Docker: environment pointing at the record directory; In-process: ambient context; Kubernetes: ConfigMap value pointing into the projected record volume |
| `COHESION_DESCRIBE_PATH` | Absolute file path | Only for the SDK's build-time describe run, never by a gateway | Set by the SDK on the describe process; the resource writes its runtime surface there and exits with code 0 when its host is built |
| `COHESION_GATEWAY` | `local`, `inprocess`, `docker`, or `kubernetes` | Set by a gateway; unset means there is no gateway | Local/Docker: environment; In-process: ambient context; Kubernetes: ConfigMap |
| `COHESION_ENVIRONMENT` | Environment name; `Local` identifies a developer machine and `Development` is deployable; falls back to `DOTNET_ENVIRONMENT`, then `Production` | Optional override | Local/Docker: environment; In-process: ambient context; Kubernetes: ConfigMap |
| `COHESION_CONTENT_ROOT` | Absolute directory path | Set when the gateway assigns a resource content root | Local/Docker: environment; In-process: ambient context; Kubernetes: ConfigMap |
| `COHESION_APPLICATION_TRUST_KEY` | Public ECDSA P-256 key as JWK | Set when the application gateway has established its trust key | Local/Docker: environment; In-process: ambient context; Kubernetes: ConfigMap |
| `COHESION_ENDPOINT_<EP>_HOST` | Bind host or IP address | For every declared endpoint; the gateway is the sole writer | Local: allocated loopback host; In-process: ambient loopback address; Docker: container bind host; Kubernetes: ConfigMap for the declared container port |
| `COHESION_ENDPOINT_<EP>_PORT` | Decimal port, 1-65535 | For every declared endpoint; gateway allocation is persisted in Local/In-process | Local: allocated loopback port; In-process: ambient context; Docker: declared container port; Kubernetes: ConfigMap and Service target port |
| `COHESION_ENDPOINT_<EP>_SCHEME` | URI scheme | For every declared endpoint | Local/Docker: environment; In-process: ambient context; Kubernetes: ConfigMap |
| `COHESION_ENDPOINT_<EP>_PUBLIC_URL` | Absolute URI | When the declared endpoint has an externally advertised URL | Local/Docker: environment; In-process: ambient context; Kubernetes: ConfigMap populated from the chosen exposure |
| `COHESION_DEPENDENCY_<RES>_<EP>_URL` | Absolute observed endpoint URI | After a required or present optional dependency reaches `Running`; absent for an optional missing dependency | Local: loopback URI; In-process: ambient loopback URI; Docker: container-network URI; Kubernetes: Service DNS URI |
| `COHESION_DEPENDENCY_<RES>_<EP>_HOST` | Observed host or IP address | With the matching observed dependency URL | Local: `127.0.0.1`; In-process: ambient loopback host; Docker: container name; Kubernetes: `<res>.<ns>.svc` |
| `COHESION_DEPENDENCY_<RES>_<EP>_PORT` | Decimal port, 1-65535 | With the matching observed dependency URL | Local/Docker: environment; In-process: ambient context; Kubernetes: ConfigMap |
| `COHESION_DEPENDENCY_<RES>_<EP>_SCHEME` | URI scheme | With the matching observed dependency URL | Local/Docker: environment; In-process: ambient context; Kubernetes: ConfigMap |
| `COHESION_MOUNT_<M>_PATH` | Absolute mounted path | For every declared mount after its source is resolved by the gateway | Local: environment pointing under `.cohesion/<app>/<res>/<mount>`; In-process: ambient handle/value; Docker: environment pointing into a volume or tmpfs; Kubernetes: ConfigMap value pointing into a PVC, ConfigMap, or Secret volume |
| `COHESION_CONFIG__<Section>__<Key>` | Configuration value; `__` maps to `:` | For every setting bridged into the resource | Local/Docker: environment; In-process: ambient context; Kubernetes: ConfigMap |
| `COHESION_BOOTSTRAP_TOKEN_PATH` | Path to a file containing the resource's bootstrap bearer credential; by default an ES256 JWT signed by the application trust key (see [Resource credentials](#resource-credentials)) | When the gateway has minted the resource bootstrap credential; rotated on every reconcile | Local: protected file; In-process: credential value in ambient context; Docker: tmpfs file; Kubernetes: Secret volume |
| `COHESION_TRUST_BUNDLE_PATH` | Path to a PEM bundle of trust-anchor certificates (no private keys) | When the gateway has issued or is brokering transport trust anchors for the application | Local: protected file; In-process: ambient context value; Docker: tmpfs file; Kubernetes: ConfigMap or Secret volume |
| `COHESION_STOP_EVENT` | Windows named-event identifier | Only for a Windows local out-of-process resource launched with the named-event stop channel | Local Windows: environment; In-process: outer host signal; Docker/Kubernetes: not set |
| `COHESION_TELEMETRY_ENDPOINT` | Absolute OTLP collector URI | Optional and reserved in v1; Hosting uses it when configured | Local/Docker: environment; In-process: ambient context; Kubernetes: ConfigMap |
| `COHESION_TELEMETRY_PROTOCOL` | `otlp-grpc` or `otlp-http` | Optional; otlp-http is implemented as OTLP/HTTP JSON; otlp-grpc remains contract-valid but this build refuses it | Local/Docker: environment; In-process: ambient context; Kubernetes: ConfigMap |
| `COHESION_TELEMETRY_HEADERS_PATH` | Path to an OTLP headers file | Optional and reserved in v1 when collector headers are required | Local: protected file path; In-process: ambient context; Docker: tmpfs file; Kubernetes: Secret volume |
| `COHESION_LOG_FORMAT` | `json`; unset means ordinary stdout/stderr text | Optional and reserved in v1 | Local/Docker: environment; In-process: ambient context; Kubernetes: ConfigMap |

## Name construction

`<EP>`, `<RES>`, and `<M>` use one ordinal upper-snake rule: ASCII `a`-`z` becomes
`A`-`Z`, ASCII `A`-`Z` and `0`-`9` are retained, and every other character becomes one
underscore. Separators are not collapsed, so `orders--db` becomes `ORDERS__DB`.

Configuration section and key names retain their declared casing. A double underscore is the
section separator because the configuration bridge maps `__` to `:`.

## Endpoint URI shape

Every value written to a `COHESION_*_URL` or `COHESION_*_PUBLIC_URL` variable uses the canonical
Cohesion endpoint form `scheme://host:port[/path]`: the URI is absolute, the host is present, the
port is explicit and between 1 and 65535, and user information, query strings, and fragments are
not allowed. The root path has no trailing slash; a non-root path is percent-encoded. Gateways and
other contract writers use `Uri.ToEndpointString()` as the single writer form.

Core readers validate this shape through `Uri.TryParseEndpoint` and return `System.Uri`. Code that
passes the host to a socket API uses `Uri.IdnHost`, which removes IPv6 brackets and converts an
internationalized domain name to its ASCII-compatible form.

## Topology notes

- An HTTPS endpoint names an ordinary Secret mount (default `tls`) containing one PEM document. The leaf is the first CERTIFICATE block; exactly one PRIVATE KEY, EC PRIVATE KEY, or RSA PRIVATE KEY block may appear anywhere. Further certificates form the issuer chain and may include a self-signed root. An empty file means no certificate. Trust anchors travel separately through `COHESION_TRUST_BUNDLE_PATH`, never as a declared mount.

- The gateway is the sole writer of declared endpoint bind values. With no gateway, item 12's
  ambient resource context falls back to the declared development port so standalone `dotnet run`
  remains stable; this document does not introduce that context.
- Dependency values are derived from the observed view, never guessed from desired state, and
  appear only after the dependency is running. Optional references do not gate startup and are
  omitted unless their target is already running when the dependent is prepared.
- Composite manifests flatten re-exported endpoints and mounts as `<member>-<name>`. An outer
  gateway combines those names with the Composite resource name, producing
  `COHESION_DEPENDENCY_<COMPOSITE>_<MEMBER>_<EP>_*` and
  `COHESION_MOUNT_<COMPOSITE>_<MEMBER>_<M>_PATH`; the claim path remains rooted under the
  Composite resource's mount directory.
- Mount and bootstrap sources are resolved by the gateway. Resources read the delivered value
  and do not pull secrets or configuration from an orchestrator.
- The optional telemetry variables are honoured by Hosting.Telemetry in local and in-process resource hosts.
  Headers use the bootstrap-credential carrier: a protected file locally and in-process, tmpfs in Docker,
  and a Secret volume in Kubernetes. With no telemetry endpoint, existing logging behavior is unchanged.
  This build exports logs as OTLP/HTTP JSON; protobuf, gRPC, traces and metrics remain deferred.
  The gateway injects these values only for the sink the application registers
  (`ResourceTelemetrySink.FromResource(...)` or `ResourceTelemetrySink.External(...)`); it never
  discovers a sink by resource kind. Gateway telemetry tokens for a resource sink carry aud=the sink
  resource (LogSpace, for example), sub=emitter and scope=telemetry; they cannot authorize sink
  management or query.

## Resource credentials

`COHESION_BOOTSTRAP_TOKEN_PATH` names a file holding the resource's bootstrap **bearer credential**:
an opaque value the resource presents as `Authorization: Bearer <credential>` and receives on its own
control plane. The contract fixes the carrier, not the credential format.

By default the credential is the **application-key credential**: a compact ES256 JWT signed by the
application trust key whose public JWK is `COHESION_APPLICATION_TRUST_KEY`. Its JOSE header names
`alg=ES256` and the key's `kid` (the RFC 7638 thumbprint); its payload carries `iss` = the application,
`sub` = the calling identity (the gateway for bootstrap, management, and store-access credentials; the
emitting resource for telemetry credentials), `aud` = the receiving resource, and `exp`, `nbf`, `iat`,
and `jti`. A resource accepts it only when the signature verifies with that key, the issuer is trusted,
`jti` is present, `iat` is no later than now plus a five-minute skew, `exp` follows both `iat` and `nbf`,
and `exp - iat` is at most 24 hours; an authentic credential for another audience is refused with 403.
Credentials presented to a gateway control plane use `aud=cohesion-export` and an 8-hour ceiling, and a
`cohesion_token_use=gateway` claim grants command dispatch. Telemetry credentials add `scope=telemetry`
and authorize only ingestion: every resource refuses the scope with 403 on any route that is not a
telemetry ingestion endpoint, whichever resource the application registered as its sink, and a
registered verifier must do the same. The claim names and ceilings are `Assimalign.Cohesion.Hosting.Resources.ResourceCredentialProfile`;
the verification mechanics are the shared ES256 validator in `Assimalign.Cohesion.IdentityModel.Token.JsonWebToken`.

An application whose gateway registers a **different credential issuer** (an identity provider, for
example, assigned to `ApplicationProviders.CredentialIssuer`) must register a matching verifier on its
resources. The issuer may return no credential for a purpose, and the gateway then mints the default
application-key credential for that purpose. A resource executable registers its verifier with
`ResourceRuntime.RegisterCredentialVerifier(Assembly, Func<ResourceContext, IResourceCredentialVerifier>)`
before building its area application; the area hosting module consults it first for every presented
credential. A `NoResult` verdict falls through to the default application-key verification, so the two
can coexist; any other verdict is final. The verifier maps the credential to a `ResourceCaller`
(application, subject, kind), and the resource authorizes that caller exactly as it authorizes an
application-key caller. The gateway's readiness and health probes present the resource's
**bootstrap** credential (purpose `ResourceBootstrap`), not the `ResourceAccess` credential it
delivers commands with, so the verifier must map the bootstrap credential to the gateway caller as
well. `COHESION_APPLICATION_TRUST_KEY` remains set as specified either way.

This is an amendment to v1 made in place on 2026-09-25 (owner decision 2, recorded as R8 in the
[developer-experience design](DEVELOPER_EXPERIENCE_DESIGN.md)), not a v2: the variable name, its
carrier, and the default credential are unchanged, and an application that registers no issuer sees
exactly the behavior above. A verifier
registration is keyed by the resource's entry assembly and can be made once per process: a second
registration for the same assembly throws. An in-process restart that re-runs the entry point
currently hits that limit, which the [developer-experience design](DEVELOPER_EXPERIENCE_DESIGN.md)
records as open follow-up O42.

## Declarative resource commands

**`cohesion/resource/v2` removed the manifest's command catalog** (2026-10-24). A v2 manifest has no
`commands` member, the gateway no longer refuses a kind its target's manifest does not list, and
`requiresInputResolver` went with the catalog: input resolvers are explicit registrations the
declaring application makes, and the receiving handler validates the payload it is delivered. The
target's control plane still lists the kinds it accepts at `GET <controlPlane>/commands`, and the
envelope, routes and refusals below are unchanged. The owner's approved direction replaces commands
with deployment-record claims and resource actions ([Deployment record](#deployment-record)); until
that lands, the paragraph that follows describes v1 manifests only.

A v1 manifest's `commands` lists each accepted kind as a string or, for a kind whose declared
payload the delivering gateway must resolve before delivery, as the object
`{ "kind": "…", "requiresInputResolver": true }`. A manifest that marks no kind is the same string
array as before (`assets/schemas/cohesion.resource.schema.json`). When the target's manifest marks
a kind, the declaring application's `Build()`, or an application set starting that member, fails
unless that application registers an `IResourceCommandInputResolver` for the kind.
`secretstore.add-secret` is marked, and the store still rejects an unresolved payload. This is an
amendment to v1 made in place on 2026-09-28 (R11 in the
[developer-experience design](DEVELOPER_EXPERIENCE_DESIGN.md)), not a v2. The proving kinds are
`database.add-database`, `database.add-principal`, `configurationstore.set-value`, and
`configurationstore.remove-value`. Wire kinds use an area prefix and a verb-noun kebab name;
the C# verbs are `AddDatabase`, `AddPrincipal`, `SetValue`, and `RemoveValue`.
`configurationstore.add-namespace` (C# verb `AddNamespace`) landed with item 31c.

Resource command requests use the manifest's `controlPlane.endpoint` and `controlPlane.path`.
The default Database admin and ConfigurationStore API paths are `/cohesion/v1/commands`:
GET lists accepted kinds, POST applies, and additive DELETE removes an owned declaration using
the same JSON envelope: `id`, `kind`, `owner`, `key`, and Base64 `payload`. These fields remain
stable. A blank `key` is invalid at graph build, direct control-plane dispatch, and HTTP ingress;
HTTP ingress returns 400. Payload JSON is serialized with generated metadata and canonicalized
before deriving the command id from kind, target identity, and payload.

Refusals carry JSON `{ "status": "Rejected", "detail": "..." }`. Unknown kinds return 501;
handler refusals return 409. ConfigurationStore retains its existing 403 owner mismatch and
404 missing-value responses, now with the same detail fields. Database currently rejects
`database.add-principal` with a named detail because its live engine has no principal mutation
API; principals remain code-first schema declarations. Existing databases outside the command
ledger cannot be adopted or deleted by an add-database declaration.

The claiming gateway applies commands after the target is Running and before dependents
reconcile. Required rejection blocks dependents; optional rejection is observed without blocking.
Local delivery uses the gateway's generic `ResourceControlPlaneCommandClient`, which POSTs and
DELETEs this envelope at `<controlPlane>/commands` for every kind; an in-process host uses its
registered control plane directly. A remote reference uses PUT and DELETE on
`/cohesion/v1/resources/{name}/commands/{id}` at the peer gateway. Application-scoped observations
and exports carry `Applied`/`Rejected` and detail. Command observations omit payload and result
bytes; the embedded desired-state model carries command payloads for application-set round trips,
so declarations in this version must not contain secret material.

Ownership is retained until confirmed deletion; refusal never transfers a key to another owner.
Runtime ledgers are invocation-local and do not establish durable ownership after host restart.
The existing federation path retains its authorization boundary: ConfigurationStore requires
`owner` to equal the authenticated caller's application (the issuer, for an application-key
credential). A peer forwarding its provider-issued bootstrap token with a foreign owner's envelope
receives 403; an owner-preserving delegated credential contract is still required for that remote
mutation path. The commands route of `Web.Hosting`, the shared control-plane terminal
that Web, Database, and most other area hosts serve, admits only the resource's own gateway but does
not yet require `owner` to equal the caller's application; the
[developer-experience design](DEVELOPER_EXPERIENCE_DESIGN.md) records it as open follow-up O43.

## Deployment record

A gateway delivers a resource's desired configuration as a **deployment record**: the directory that
`COHESION_CONFIG_DIRECTORY` names, holding up to three kinds of layer file. The resource reads the
directory and never writes it, and it keeps no configuration anywhere else. Code stays the source of
truth: each configuration section's **baseline** is the value the resource configured itself with
(code and appsettings), and the record's layers merge over that baseline.

| File | Written from | Lives until |
| --- | --- | --- |
| `deployment.json` | The application's code, through the gateway's composition | The resource's next deployment |
| `claims.<owner>.json` | Another application's code; `<owner>` is that application's name, an RFC 1123 label | The owner withdraws the claim or the resource is deleted |
| `override.json` | The gateway's management API | The next deployment that changes the resource's deployment content |

Each file is a UTF-8 JSON object of at most 1 MiB. Its members are configuration paths (`Web:Cors`),
and each value is an RFC 7396 merge patch for that section; a `null` value resets the section to its
baseline. A section's effective value is its baseline with the deployment layer, each claim layer in
ordinal order of owner, then the override layer merged over it. Any other file in the directory is
ignored, and so is a `claims.*.json` whose owner is not a label. Re-applying identical deployment
content keeps the override layer; deployment content that changed discards it. A resource's deployment
content is its realization plan, which carries the deployment layer, and the build it deploys; the
gateway names each distinct content a generation.

The record's identity is its digest, `sha256:<lowercase hex>`, over the exact bytes of every file
present. For each file in the order deployment, claims by ordinal owner, override, the hash reads the
file name in UTF-8, one zero byte, the file length as a big-endian 64-bit integer, then the file's
bytes. Reformatting a file changes the digest; there is no canonical form, so the writer computes the
digest from the bytes it writes.

A resource applies the record in two phases:

- **Startup.** Before its host starts, every section applies, whatever its mutability. A section the
  resource refuses keeps its baseline and the resource stays unready (readiness 503, liveness
  unaffected), so a rolling update stops at its first instance.
- **Running.** The resource rereads the directory every 2 seconds and accepts a read only when two
  consecutive reads agree, so a platform that replaces files one at a time is never seen half
  written. A section whose mutability is `live` applies; any other changed section reports
  `restartRequired` and keeps its value until the instance restarts.

Every instance reports what it applied at `GET <controlPlane>/configuration/status`:

```json
{
  "instance": "<COHESION_RESOURCE_INSTANCE, when set>",
  "surfaceDigest": "sha256:…",
  "configuration": {
    "recordDigest": "sha256:…",
    "sections": [ { "path": "Web:Cors", "state": "applied" } ]
  }
}
```

Section states are `baseline`, `applied`, `rejected` (with an `error`), and `restartRequired`;
`recordDigest` is `null` until a record is applied. The health contribution `cohesion.configuration`
is Degraded while any section is rejected or waiting for a restart.

`GET <controlPlane>/configuration` returns the resource's **runtime surface**, a
`cohesion/runtime/v1` document listing its configuration sections (path, mutability `deployment`,
`restart` or `live`, scope `process` or `backend`, whether other applications may claim entries, and
the JSON schema of the section's value), its actions, and its tools. The surface is fixed when the
host is built. After each build, the SDK's describe run sets `COHESION_DESCRIBE_PATH` so the
resource writes the same document and exits as soon as its host is built; the SDK keeps it as
`resource.runtime.json` beside the build output and in the resource's manifest package. The run
removes inherited `COHESION_*` variables and gives each declared reference endpoint a placeholder
`COHESION_DEPENDENCY_<RES>_<EP>_URL` at the reserved host `cohesion-describe.invalid`, so a resource
that reads its references while composing can be described without reaching anything.
`POST <controlPlane>/actions/{name}` runs an action with a
JSON body and returns 200 with its JSON result, 400 for input that does not bind, and 404 for an
unknown action. These routes are control-plane routes, authorized like every other namespaced route.

Platforms deliver the directory without the resource knowing which platform it is on. A Local or
Docker gateway writes the files into a directory it owns and passes (Docker bind-mounts it); the Local
and in-process gateways keep each resource's record in
`<state>/<application>/<resource>/.state/record/` and pass it to every resource they realize, with or
without deployment configuration, so a later management change reaches it. A Kubernetes gateway
projects the deployment's per-generation ConfigMap and the resource's claims ConfigMap into one
directory volume, never with `subPath`, which would freeze the files at pod start.

This is an amendment to v1 made in place on 2026-10-24 (owner decisions D1–D8 of the
[deployment-record control plane](programs/RESOURCE_CONTROL_PLANE_REDESIGN.md)), not a v2: it adds
`COHESION_CONFIG_DIRECTORY` and `COHESION_DESCRIBE_PATH`, and gives `COHESION_RESOURCE_INSTANCE` a
defined use. Hosting.Resources implements the resource side and the SDK the describe run; the Local
and in-process gateways deliver records, and platform gateways follow in the program's phase P3. The
gateway computes a record's digest with Hosting.Resources' own `ResourceConfigurationRecord.Create`,
so the digest it compares with an instance's report is computed by the instance's rule.
