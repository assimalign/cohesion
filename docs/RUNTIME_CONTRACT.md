# Cohesion Runtime Contract v1

This document is the language-neutral contract between a Cohesion gateway and a resource.
The contract is frozen at version 1: changing a variable name or its meaning is a breaking
wire change. .NET consumers use `Assimalign.Cohesion.Core.ResourceEnvironment`; non-.NET
workloads use this table directly. The typed .NET endpoint readers return `System.Uri`, and
Core's `System.UriExtensions` supplies endpoint validation, construction, parsing, and canonical
formatting without introducing a Cohesion-specific address type.

Out-of-process resources receive values through process environment variables. In-process
resources receive the same keys through the ambient resource context introduced by runtime
contract item 12. A standalone process with no gateway has `COHESION_GATEWAY` unset and resolves
its environment name as `COHESION_ENVIRONMENT ?? DOTNET_ENVIRONMENT ?? "Production"`.

## Variable contract

| Variable | Value shape | When set | Carrier by topology |
| --- | --- | --- | --- |
| `COHESION_APPLICATION` | Application name | Every gateway-realized Cohesion resource | Local: environment; In-process: ambient context; Docker: environment; Kubernetes: ConfigMap |
| `COHESION_RESOURCE` | Resource name | Every gateway-realized Cohesion resource | Local: environment; In-process: ambient context; Docker: environment; Kubernetes: ConfigMap |
| `COHESION_GATEWAY` | `local`, `inprocess`, `docker`, or `kubernetes` | Set by a gateway; unset means there is no gateway | Local/Docker: environment; In-process: ambient context; Kubernetes: ConfigMap |
| `COHESION_ENVIRONMENT` | Environment name; falls back to `DOTNET_ENVIRONMENT`, then `Production` | Optional override | Local/Docker: environment; In-process: ambient context; Kubernetes: ConfigMap |
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
| `COHESION_BOOTSTRAP_TOKEN_PATH` | Path to a file containing an ES256 JWT | When the gateway has minted the resource bootstrap credential; rotated on every reconcile | Local: protected file; In-process: credential value in ambient context; Docker: tmpfs file; Kubernetes: Secret volume |
| `COHESION_STOP_EVENT` | Windows named-event identifier | Only for a Windows local out-of-process resource launched with the named-event stop channel | Local Windows: environment; In-process: outer host signal; Docker/Kubernetes: not set |
| `COHESION_TELEMETRY_ENDPOINT` | Absolute OTLP collector URI | Optional and reserved in v1; Hosting uses it when configured | Local/Docker: environment; In-process: ambient context; Kubernetes: ConfigMap |
| `COHESION_TELEMETRY_PROTOCOL` | `otlp-grpc` or `otlp-http` | Optional and reserved in v1; meaningful when a telemetry endpoint is set | Local/Docker: environment; In-process: ambient context; Kubernetes: ConfigMap |
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
- The telemetry variables are reserved now but remain optional. When none are set, logging is
  stdout/stderr only.

## Declarative resource commands

An enabled manifest's `commands` remains a bare string array. The proving kinds are
`database.add-database`, `database.add-principal`, `configurationstore.set-value`, and
`configurationstore.remove-value`. Wire kinds use an area prefix and a verb-noun kebab name;
the C# verbs are `AddDatabase`, `AddPrincipal`, `SetValue`, and `RemoveValue`.
`configurationstore.add-namespace` is deferred for orchestration to schedule under item 31c.

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
Local delivery uses the area's Client package; an in-process host uses its registered control
plane directly. A remote reference uses PUT and DELETE on
`/cohesion/v1/resources/{name}/commands/{id}` at the peer gateway. Application-scoped observations
and exports carry `Applied`/`Rejected` and detail. Command observations omit payload and result
bytes; the embedded desired-state model carries command payloads for application-set round trips,
so declarations in this version must not contain secret material.

Ownership is retained until confirmed deletion; refusal never transfers a key to another owner.
Runtime ledgers are invocation-local and do not establish durable ownership after host restart.
The existing federation path retains its authorization boundary: ConfigurationStore requires
`owner` to equal the authenticated issuer. A peer forwarding its provider-issued bootstrap token
with a foreign owner's envelope receives 403; an owner-preserving delegated credential contract
is still required for that remote mutation path.
