# Cohesion Runtime Contract v1

This document is the language-neutral contract between a Cohesion gateway and a resource.
The contract is frozen at version 1: changing a variable name or its meaning is a breaking
wire change. .NET consumers use `Assimalign.Cohesion.Core.ResourceEnvironment`; non-.NET
workloads use this table directly.

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
