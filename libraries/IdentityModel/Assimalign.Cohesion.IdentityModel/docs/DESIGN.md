# Assimalign.Cohesion.IdentityModel — Design

## Design intent

One canonical authentication model for the whole platform. Every Cohesion
resource that authenticates a caller consumes the same subject, claim,
session, and authentication-result contracts, regardless of whether the
authentication was driven by OpenID Connect, SAML 2.0, or a protocol added
later. The root package is the **dependency anchor** of the family: protocol
contracts and the canonical domain model live here; descendant packages add
concrete token-document behavior on top and never define parallel
foundational abstractions.

This library is a shared blocker for IdentityHub and for every resource-layer
service that needs authentication. Getting the contracts right here is what
lets those services stay protocol-agnostic.

Three tenets shape everything in the family:

1. **Descriptive, not executable.** The root library models identity and
   protocol *data* — messages, metadata, descriptors, results. It does not
   open connections, parse wire formats, execute cryptography, or orchestrate
   protocol flows. Those belong to implementation packages and services that
   build on these contracts.
2. **Normalization preserves provenance.** Mapping protocol-specific data
   (OIDC claims, SAML attributes) into the canonical model never erases where
   the data came from. Canonical claims carry protocol provenance and original
   identifiers so auditing, interop, and provider-migration scenarios keep
   their source meaning.
3. **No protocol is primary.** The canonical model must represent an
   OIDC-authenticated principal and a SAML-asserted principal with equal
   fidelity. Anything that privileges one wire format over the other belongs
   in that protocol's branch, not in the shared model.

## Family map

| Package | Role | Cohesion dependencies |
|---------|------|-----------------------|
| `Assimalign.Cohesion.IdentityModel` | Canonical identity domain model + shared protocol abstractions + OIDC and SAML 2.0 contract branches | none |
| `Assimalign.Cohesion.IdentityModel.Token` | Protocol-neutral token/assertion normalization layer | `…IdentityModel` |
| `Assimalign.Cohesion.IdentityModel.Token.JsonWebToken` | Concrete JOSE / JWT document behavior | `…IdentityModel.Token` |
| `Assimalign.Cohesion.IdentityModel.Token.Saml` | Concrete SAML assertion token behavior | `…IdentityModel.Token` |

Dependency direction is one-way toward the root. Sibling token packages never
reference each other. These rules are enforced by architecture tests in
`Assimalign.Cohesion.IdentityModel.Tests` (`IdentityModelFamilyBoundaryTests`),
which fail the build the moment a package grows a reference that violates the
direction, references a sibling, or picks up any `Microsoft.Extensions.*`
dependency.

### Ownership boundaries

- **Root (`…IdentityModel`)** owns: the canonical identity domain model
  (subjects, application identities, actors, credentials, claims and
  attributes, sessions, authentication context, authentication results); the
  shared protocol abstractions (party roles, protocol metadata, message
  envelopes, validation results, logout semantics, binding descriptors); and
  the first-class protocol contract branches for OpenID Connect and SAML 2.0.
- **`…IdentityModel.Token`** owns: the protocol-neutral token/assertion
  normalization contracts — what it means to be "a token" (issuer, subject,
  audiences, temporal validity, normalized claims, provenance) independent of
  wire format — plus reader/writer/validator seams that are genuinely
  protocol-neutral.
- **`…Token.JsonWebToken`** owns: concrete JWT document fidelity — JOSE
  header shape, compact serialization, registered and OIDC ID-token claims,
  JWT-specific validation descriptors and negative-case behavior.
- **`…Token.Saml`** owns: concrete SAML assertion token fidelity —
  statements, conditions, subject confirmation, NameID semantics, raw
  assertion context.

Two placement rules resolve every "where does this type go?" question:

1. If a concept is a *protocol contract* (message shape, metadata shape,
   validation semantics), it lives in the root — even when only one protocol
   uses it today.
2. If a concept is *concrete document behavior* for one token format, it
   lives in that format's descendant package.

### Extension guidance (future packages)

Implementation packages — protocol readers/writers, metadata document
handlers, validators that execute cryptography — may be added later as
descendants (for example `…IdentityModel.Protocols.OpenIdConnect.Metadata` or
`…IdentityModel.Saml.Serialization`). When they are:

- They depend on the root (and on `…Token` where token materialization is
  involved); the contracts they implement stay in the root library.
- They never introduce a second foundational abstraction for a concept the
  root already models.
- They register in `frameworks/Assimalign.Cohesion.App.props` alongside the
  existing family members and get an entry in the family boundary tests.

## Namespace map

The root assembly uses namespace branches under the assembly-name root, so
protocol-spec ownership is obvious from the namespace alone:

| Namespace | Contents |
|---|---|
| `Assimalign.Cohesion.IdentityModel` | Canonical identity domain model: subjects, application identity, actors, credentials, claims/attributes, sessions, authentication context and results; the `IdentityModelException` error root. |
| `Assimalign.Cohesion.IdentityModel.Protocols` | Cross-protocol abstractions: protocol parties and roles, protocol metadata, request/response envelopes, validation results, logout semantics, binding and endpoint descriptors. |
| `Assimalign.Cohesion.IdentityModel.Protocols.OpenIdConnect` | OpenID Connect contracts: issuer/discovery metadata, client metadata and registration shape, authorization/token/ID token/UserInfo/logout message contracts, OIDC constants. |
| `Assimalign.Cohesion.IdentityModel.Protocols.Saml` | SAML 2.0 contracts: assertion content (subject, NameID, statements, conditions, confirmations), protocol messages (AuthnRequest, Response, Status, logout), entity metadata, binding descriptors, SAML constants. |

Folder layout under `src/` mirrors the namespace branches
(`src/Protocols/OpenIdConnect/…`, `src/Protocols/Saml/…`), with the repo's
standard `Abstractions/`, `Extensions/`, `Internal/`, and `Exceptions/`
folders inside each area where applicable.

Descendant assemblies keep the repo rule that namespace == assembly name:
`Assimalign.Cohesion.IdentityModel.Token`,
`…Token.JsonWebToken`, `…Token.Saml`. The
`IdentityModelNamespaceAlignmentTests` guardrail asserts every public type in
every family assembly lives under its assembly-name namespace.

## Modeling conventions

- **Interface-first public surface.** Consumer-facing contracts are
  interfaces (`IIdentitySubject`, `IIdentityClaim`, …) with `internal` or
  `sealed` implementations, following the repo-wide pattern. Data-shaped
  protocol types (metadata documents, message contracts) may be sealed
  immutable classes when there is exactly one meaningful shape and an
  interface would add ceremony without a second implementation ever existing.
- **Descriptor → immutable model.** Non-trivial models follow the existing
  token pattern: a mutable descriptor for construction, an immutable model
  that snapshots the descriptor. Mutation after construction never leaks into
  a materialized model.
- **Validation failures are values, not exceptions.** Protocol validation
  produces `…ValidationResult` values carrying normalized diagnostics
  (severity, code, message) rich enough for compliance testing. Exceptions —
  rooted at the area-scoped `IdentityModelException` — are reserved for
  contract misuse (invalid arguments, invalid state transitions), not for
  "the token failed validation".
- **Constants over stringly-typed guesses.** Well-known protocol values
  (claim names, scopes, NameID formats, binding URIs, status codes) ship as
  constant classes in the owning protocol branch so consumers never
  hand-spell spec values.

## Error model

`IdentityModelException` (root namespace) is the area-scoped exception root,
inheriting directly from `Exception` per the repo rule — no framework-wide
ancestry. Protocol branches add specific exception types only when a failure
is a programming error rather than a validation outcome. The bar for adding
an exception type: would a correct program ever catch it to make a decision?
If the answer is "no, it should have produced a validation result instead",
it's not an exception.

## AOT posture

`<IsAotCompatible>true</IsAotCompatible>` applies to the whole family via
`libraries/Directory.Build.props`. The contracts are deliberately:

- Plain-old CLR object models — no serializer dependencies in any shipped
  assembly, no reflection-based inference on public paths, no runtime code
  generation. (The existing `IdentityTokenClaim` value-kind inference uses
  type pattern matching, which is trim-safe.)
- Free of open-generic factory patterns that would require reflection emit.
- Serialization-agnostic: consumers bring their own source-generated
  serializers. The JWT package may use `System.Text.Json`'s reader/document
  APIs (`Utf8JsonReader`, `JsonDocument`) for compact-serialization parsing —
  these are reflection-free and AOT-safe — but never
  `JsonSerializer` over unannotated object graphs.

Representative NativeAOT publish validation for the family is a first-class
deliverable of the epic's final feature and its evidence is recorded in the
family docs when it lands.

## Standards references

The contract surfaces are shaped by, and compliance fixtures trace back to:

- **OpenID Connect**: Core 1.0, Discovery 1.0, Dynamic Client Registration
  1.0, RP-Initiated Logout 1.0.
- **OAuth 2.0**: RFC 6749, PKCE (RFC 7636), and the OAuth 2.0 Security Best
  Current Practice, informing authorization-flow and client contract design.
- **SAML 2.0**: Core, Bindings, Profiles, and Metadata (OASIS).
- **JOSE / JWT**: RFC 7515 (JWS), RFC 7517 (JWK), RFC 7518 (JWA), RFC 7519
  (JWT), RFC 8725 (JWT Best Current Practices).

Authorization Code with PKCE is the primary modeled OIDC flow; implicit-era
shapes are represented only where compatibility requires them.

## Consumption guidance

Downstream services (IdentityHub, Web, Database, …) should:

- Depend on the root contracts for identity decisions — never on a concrete
  token package directly, unless the service genuinely handles that wire
  format.
- Treat canonical claims as the authorization input surface; protocol
  provenance is available on every normalized claim when a decision must be
  protocol-aware.
- Never define service-local subject/claim/session types for concepts this
  family models. If the family is missing a concept, it gets added here (with
  a work item), not re-invented downstream.

## Non-goals

Explicitly out of scope for this family (deliberate, revisit only with a new
work item):

- **Protocol flow orchestration and transport execution.** No HTTP clients,
  redirect handling, or endpoint hosting. Binding descriptors describe
  transports; they never execute them.
- **Cryptographic execution.** Signature creation/verification, key
  management, and encryption are seams (descriptors, validation parameters),
  not implementations. Executing crypto belongs to future implementation
  packages that can layer on the Security area.
- **Wire-format serialization for SAML.** The family preserves raw assertion
  XML as opaque context; XML readers/writers are a future implementation
  package.
- **Tenancy contracts.** The subject model leaves room for tenant scoping
  (extensible identifiers and attributes) but defines no tenant type; the
  earlier backlog's tenant/directory concepts were superseded and deferred.
- **An authorization model.** Roles, permissions, and policy are downstream
  concerns built on canonical claims; only protocol-defined authorization
  *data* (scopes, authorization context) is modeled.
- **SCIM 2.0 provisioning contracts.** Deferred; the claim/attribute model
  stays compatible with SCIM-style multi-value attributes so a future SCIM
  branch can map cleanly.
- **JWE (encrypted tokens).** RFC 7516 document shapes are deferred; the JWT
  package models signed compact serialization (JWS) and records JWE as a
  deferred row in its compatibility matrix.

## Delivery roadmap

The family is delivered through Epic `[L01.01.12]` (#105). Feature order is
dependency order; this document is updated whenever a landed feature refines
a decision recorded here:

| Feature | Scope |
|---|---|
| `[L01.01.12.01]` #584 | This document, namespace map, family boundaries, architecture validation. |
| `[L01.01.12.02]` #588 | Canonical identity domain model (subjects, credentials, sessions, results, claims). |
| `[L01.01.12.03]` #592 | Shared protocol abstractions (`Protocols` namespace). |
| `[L01.01.12.04]` #596 | OpenID Connect contract branch. |
| `[L01.01.12.05]` #600 | SAML 2.0 contract branch. |
| `[L01.01.12.06]` #604 | Token normalization alignment with the root model. |
| `[L01.01.12.07]` #608 | JWT package to OIDC grade. |
| `[L01.01.12.08]` #612 | SAML token package to assertion grade. |
| `[L01.01.12.09]` #616 | Cross-protocol claim mapping, migration fixtures, compliance matrices, NativeAOT evidence. |
