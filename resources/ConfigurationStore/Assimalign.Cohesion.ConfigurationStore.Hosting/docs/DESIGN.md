# Assimalign.Cohesion.ConfigurationStore.Hosting Design

## Design intent

The hosting module implements the area root's contract-only application seam. `ConfigurationStoreApplication.CreateBuilder(args)` returns the public concrete `ConfigurationStoreApplicationBuilder`; its `Build()` returns the public `ConfigurationStoreApplication : Host<ConfigurationStoreApplicationContext>`. The public `ConfigurationStoreApplicationContext` implements `IConfigurationStoreApplicationContext`, reading `ContentRootPath` from the host environment. The application explicitly forwards the root lifecycle contract to `IHost`, and consumers use the concrete application for `RunAsync` and `await using`. Runtime options and supporting services remain internal.

## Execution model

The builder captures code-first namespace declarations, materializes explicit services once, and
appends the ConfigurationStore HTTP endpoint. Explicit services therefore start before the listener
and stop after it. An enabled resource consumes its ambient `ResourceContext`, generated default
control plane, `api` endpoint, `data` volume, environment, application trust key, and bootstrap
credential. With no generated registration it remains a plain application and accepts `--endpoint`
and `--data` overrides (defaulting to `http://127.0.0.1:8080` and `data` under the content root).

## Persistence and protocol

Each namespace is a plain JSON document beneath `data/namespaces`; its file name is the SHA-256
digest of the namespace name, while the document retains the original name. Writes use a temporary
file and atomic replacement. A declaration is written only when that namespace is absent, so values
set or removed through `POST /cohesion/v1/commands` survive restart. Configuration values are not
secret material and are intentionally stored unencrypted. A mutation is committed to the live
snapshot only after its durable replacement succeeds, so a failed write cannot create restart drift.

`GET /cohesion/v1/namespaces` lists names and `?name=` reads one direct key/value object. Commands
use `configurationstore.set-value` with JSON payload `{ "value": string|null }` or
`configurationstore.remove-value`; the command key is `<namespace>/<key>` and splits at the last
slash. Health, readiness, liveness, observed endpoints, and graceful stop share the standard
`/cohesion/v1` control-plane surface.

## Trust

Gateway-managed hosts require ES256 bearer JWTs. On first start, `data/trust/trusted-issuers.json`
is seeded with the ambient application's public JWK. Later starts load that durable issuer set and
replace the application's entry when the ambient gateway trust key has rotated.
The current `ResourceContext` does not expose peer trust-grant issuers, so this implementation cannot
seed cross-application grants until that hosting seam is defined. Validation requires the exact issuer,
P-256 signature and RFC 7638 `kid`, target-resource audience, `iss/sub/aud/exp/nbf/iat/jti`, and a
maximum 24-hour lifetime. Missing or invalid credentials return 401; a valid credential for another
audience or a command whose owner differs from its issuer returns 403. Plain applications do not
require authentication.

## Boundaries

The module references only the ConfigurationStore area root among resource packages. Hosting,
Hosting.Resources, Hosting.Health, IdentityModel JWT primitives, and the private Web transport are
infrastructure dependencies; no Gateway or other ConfigurationStore feature package is referenced.
JSON is parsed and written explicitly, with no reflection-based serialization.

The SDK default declares HTTPS with the `tls` Secret mount. The enabled host binds that endpoint
using the shared certificate contract; explicit HTTP endpoints remain supported.


## Declarative command delivery

Configuration commands now register runtime handlers on the same IResourceControlPlane used by direct
in-process delivery. The HTTP adapter authenticates first, preserving issuer/owner equality (403),
missing namespaces (404), and unsupported kinds (501), with status/detail JSON on command refusals.
Other ownership refusals return 409. POST continues to accept the existing envelope and set payload
{value}; typed declarations can additionally include namespace/key, which must match the envelope key.
Configuration keys cannot contain `/`; namespaces may contain it. This keeps the final-slash
ownership identity unambiguous for typed, direct, and HTTP declarations.
DELETE commands uses the same envelope: removing a set declaration removes its value; removing a
remove-value declaration releases ownership without restoring an undeclared historical value.

The shared command ledger is invocation-local and does not persist ownership across resource restarts.
The repository still durably stores values. Cross-application delegation is not inferred from a
local issuer's bootstrap credential; the existing issuer/owner guard remains enforced.

## Declarative commands (item 31c)

| Wire kind | Descriptor verb | Ownership key |
|---|---|---|
| `configurationstore.add-namespace` | `AddNamespace` | namespace name |

Kinds use verb-noun kebab under the area prefix. The examples `rezolvr.record` and
`identityhub.audience` in developer-experience design section 7 are illustrative; item 27's design
rewrite should reflect the landed convention. Manifest commands remain bare JSON strings.
Typed verbs validate argument shape and use source-generated JSON metadata. Build validates the
advertised kind, canonical payload, deterministic id, and uniqueness of the target ownership key.
The default control plane handles id replay and owner isolation; each area handler also accepts
an identical reapplication with a different id. Conflicts return named Rejected details.

AddNamespace creates a namespace if absent and atomically stores its owner and original seed
alongside values. An identical declaration succeeds even after separate value commands change its
contents. A different seed or foreign owner is rejected with a named detail. Resource-seeded namespaces
are not implicitly adopted. Deletion removes the owned namespace; callers should remove its value
commands first. Existing SetValue and RemoveValue behavior remains unchanged, including 404 for
unknown namespaces.

## HTTPS endpoint certificate contract (31t)

The enabled resource's `api` listener consumes the shared Hosting.Resources endpoint certificate accessor. Endpoint metadata identifies an ordinary Secret mount (default `tls`), carrying one PEM leaf/private-key/chain document; existing hand-authored IdentityHub and LogSpace bundles retain the same format. Empty mounts are absent; malformed or multi-key bundles fail. TLS options are composed in Hosting from the returned leaf and chain, with no hosting-isolation exemptions or dependency changes. Plain application composition is unchanged.

## Optional telemetry (31b)

The registered resource constructor calls ResourceTelemetry.Configure using the invocation snapshot. With no gateway or telemetry endpoint, existing providers and hosted services are unchanged. When enabled, the shared Hosting.Telemetry sibling adds OTLP/HTTP JSON logging and a service registered before producers; reverse StopAsync drains producers before a flush bounded by five seconds and the host shutdown token. Logging remains composed only in Hosting. See libraries/Hosting/Assimalign.Cohesion.Hosting.Telemetry/docs/DESIGN.md for ordering and protocol limits.

## Concrete composition (T10 / O34)

Background-work registration belongs to the concrete `ConfigurationStoreApplicationBuilder`: `AddService(IHostService)` and `AddService(Func<ConfigurationStoreApplicationContext, IHostService>)`. The factory receives the same concrete context as Web's and Database's AddService, so hosting consumers can use environment, state, and hosted-service members beyond the small root contract. Database's root-level AddServer keeps the interface context. Factories run once per build against the same context retained by the application; the hosted-service snapshot is installed after factory evaluation. Services start in registration order and stop in reverse. No area-owned service abstraction is introduced.

The base host owns the already-cancelled run semantic: one complete start and graceful stop
with fresh lifecycle tokens, normal run-observer notifications, and a final Stopped state.
The concrete application and IHost route share it. Startup failures still roll back and propagate.
