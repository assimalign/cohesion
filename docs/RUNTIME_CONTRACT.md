# Cohesion Runtime Contract v1

This document is the language-neutral contract between a Cohesion gateway and the resource
instances it runs. The contract is frozen at version 1: changing a variable name or its meaning is
a breaking wire change. .NET consumers take the names from `AppEnvironment.Variables` in Core
(namespace `Assimalign.Cohesion`), the contract's only .NET home for every variable below and the
name builders, and read values through `AppEnvironment`'s readers (`GetValue`, `TryGetPort`,
`TryGetUri`, `TryGetEndpoint`, `TryGetDependency`, `TryGetMount`); non-.NET workloads use this
table directly. The typed .NET endpoint readers return `System.Uri`, and Core's
`System.UriExtensions` supplies endpoint validation, construction, parsing, and canonical
formatting without introducing a Cohesion-specific address type (R9 and R6 in the
[developer-experience design](DEVELOPER_EXPERIENCE_DESIGN.md)).

The variables are what a platform tells a workload. An out-of-process platform delivers them as
process environment variables (Kubernetes: the pod template's environment). The in-process
platform delivers the same keys through an ambient context: the `AppEnvironmentScope` (Core) that
`ApplicationModel.Gateway.InProcess` enters on the thread it runs each resource's program on
(2026-10-29). Inside it, `AppEnvironment` reads the scope's values, a contract variable the scope
does not set reads as unset, and a host run stops on the scope's shutdown signal instead of the
process's. A standalone process with no gateway has `COHESION_GATEWAY` unset, runs unmanaged, and
resolves its environment name as `COHESION_ENVIRONMENT ?? DOTNET_ENVIRONMENT ?? "Production"`.

How an instance uses these values (publishing its manifest, restoring state, serving the
gateway's actions) is the application model's design:
[docs/libraries/ApplicationModel/DESIGN.md](libraries/ApplicationModel/DESIGN.md).

## Variable contract

"Reserved" means the name and meaning are fixed in v1 but no gateway sets the variable today.

| Variable | Value shape | When set |
| --- | --- | --- |
| `COHESION_APPLICATION` | The name of the model the gateway runs | On every instance a gateway starts |
| `COHESION_RESOURCE` | Resource name | On every instance a gateway starts |
| `COHESION_GATEWAY` | The platform's name, such as `inprocess` or `kubernetes` | Set by a gateway to the platform it runs the instance on; the instance uses the client of that platform its resource opted into, and fails to build when it opted into none; unset means there is no gateway and the instance runs unmanaged |
| `COHESION_ENVIRONMENT` | Environment name; `Local` identifies a developer machine and `Development` is deployable; falls back to `DOTNET_ENVIRONMENT`, then `Production` | Optional override; the in-process gateway passes its own environment name when it has one |
| `COHESION_CONTENT_ROOT` | Absolute directory path | Reserved: a gateway-assigned content root |
| `COHESION_APPLICATION_TRUST_KEY` | Public ECDSA P-256 key as JWK | Reserved: the public half of the gateway's trust key |
| `COHESION_ENDPOINT_<EP>_HOST` | Bind host or IP address | For every endpoint the platform assigns; the gateway is the sole writer (in-process: `127.0.0.1`) |
| `COHESION_ENDPOINT_<EP>_PORT` | Decimal port, 1-65535 | With the matching host; the in-process gateway keeps a resource's port for its lifetime, so an update listens where its predecessor did |
| `COHESION_ENDPOINT_<EP>_SCHEME` | URI scheme | With the matching host |
| `COHESION_ENDPOINT_<EP>_PUBLIC_URL` | Absolute URI | Reserved: an externally advertised URL for the endpoint |
| `COHESION_DEPENDENCY_<RES>_<EP>_URL` | Absolute observed endpoint URI | Reserved: a dependency's observed endpoint. Today a gateway passes a dependency's address as a setting (`COHESION_CONFIG__…`) instead |
| `COHESION_DEPENDENCY_<RES>_<EP>_HOST` | Observed host or IP address | Reserved, with the matching dependency URL |
| `COHESION_DEPENDENCY_<RES>_<EP>_PORT` | Decimal port, 1-65535 | Reserved, with the matching dependency URL |
| `COHESION_DEPENDENCY_<RES>_<EP>_SCHEME` | URI scheme | Reserved, with the matching dependency URL |
| `COHESION_MOUNT_<M>_PATH` | Absolute mounted path | Reserved: a mount the gateway resolved and delivered |
| `COHESION_CONFIG__<Section>__<Key>` | Configuration value; `__` maps to `:` | For every setting the gateway's model gives the resource (`Orders:Url` → `COHESION_CONFIG__Orders__Url`) |
| `COHESION_BOOTSTRAP_TOKEN_PATH` | Path to a file containing the instance's bearer credential | Reserved: a gateway-minted credential the instance presents and accepts |
| `COHESION_TRUST_BUNDLE_PATH` | Path to a PEM bundle of trust-anchor certificates (no private keys) | Reserved: the transport trust anchors a gateway brokers |
| `COHESION_STOP_EVENT` | Windows named-event identifier | Reserved: a stop channel for a Windows out-of-process instance |
| `COHESION_TELEMETRY_ENDPOINT` | Absolute OTLP collector URI | Reserved |
| `COHESION_TELEMETRY_PROTOCOL` | `otlp-grpc` or `otlp-http` | Reserved, with the telemetry endpoint |
| `COHESION_TELEMETRY_HEADERS_PATH` | Path to an OTLP headers file | Reserved, with the telemetry endpoint |
| `COHESION_LOG_FORMAT` | `json`; unset means ordinary stdout/stderr text | Reserved |
| `COHESION_MANIFEST_ENDPOINT` | Where the instance's gateway client reaches its gateway, in the platform's form: for `inprocess`, the `inprocess://<id>/` endpoint of the gateway in the instance's own process | Set by the gateway that provisions the instance; unset means the instance is not managed |
| `COHESION_CONTROL_PLANE_ENDPOINT` | Where a platform's instances serve their control plane at an address of their own, which the gateway client names in the instance's manifest | Reserved for a platform that uses it; the in-process gateway calls its instances directly and does not set it |
| `COHESION_DEPLOYMENT` | UUID of the deployment that started the instance; the instance repeats it in every manifest it publishes, and its gateway counts a deployment ready only once an instance that repeats its id publishes | Set with `COHESION_MANIFEST_ENDPOINT` on every instance a gateway's deployment starts. Kubernetes writes it as a literal value in the pod template, so a restarted pod of an earlier rollout keeps reporting its own deployment |

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

## Contract notes

- The gateway is the sole writer of endpoint bind values. With no gateway, an application binds
  what its own code configures.
- A setting is the gateway's to resolve: the instance reads the delivered value and pulls nothing
  from an orchestrator.
