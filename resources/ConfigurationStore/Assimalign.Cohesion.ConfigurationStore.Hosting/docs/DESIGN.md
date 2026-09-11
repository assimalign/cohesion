# Assimalign.Cohesion.ConfigurationStore.Hosting Design

## Design intent

The hosting module implements the area root's contract-only application seam. Public construction is limited to `ConfigurationStoreApplication.CreateBuilder(args)`; the builder, `Host<TContext>` implementation, context, and options are internal.

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

The inherited SDK default still declares HTTPS without a certificate mount, while this host currently
has only an HTTP binder. Until the endpoint/TLS contract is completed, development manifests must
override `api` to loopback HTTP; the unresolved contract is called out in the implementation report.


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
