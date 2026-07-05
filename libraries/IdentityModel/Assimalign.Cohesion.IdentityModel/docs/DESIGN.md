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
| `Assimalign.Cohesion.IdentityModel` | Canonical identity domain model (the dependency anchor) | none |
| `Assimalign.Cohesion.IdentityModel.Protocols` | Shared, transport-agnostic protocol abstractions the contract branches build on | `…IdentityModel` |
| `Assimalign.Cohesion.IdentityModel.Protocols.OpenIdConnect` | OpenID Connect contract branch | `…IdentityModel.Protocols` |
| `Assimalign.Cohesion.IdentityModel.Protocols.Saml` | SAML 2.0 contract branch | `…IdentityModel.Protocols` |
| `Assimalign.Cohesion.IdentityModel.Token` | Protocol-neutral token/assertion normalization layer | `…IdentityModel` |
| `Assimalign.Cohesion.IdentityModel.Token.JsonWebToken` | Concrete JOSE / JWT document behavior | `…IdentityModel.Token` |
| `Assimalign.Cohesion.IdentityModel.Token.Saml` | Concrete SAML assertion token behavior | `…IdentityModel.Token` |

Two independent branches hang off the root anchor:

```
                    Assimalign.Cohesion.IdentityModel           (canonical model)
                   /                                  \
   …IdentityModel.Protocols                        …IdentityModel.Token
     /                 \                             /              \
 …Protocols.OpenIdConnect  …Protocols.Saml   …Token.JsonWebToken  …Token.Saml
```

The protocol branch (contracts: what a protocol *says*) and the token branch
(documents: what a token *is*) are deliberately independent — neither
references the other. Dependency direction is one-way toward the root; sibling
packages within a branch never reference each other. These rules are enforced
by architecture tests in `Assimalign.Cohesion.IdentityModel.Tests`
(`IdentityModelFamilyBoundaryTests`), which fail the build the moment a package
grows a reference that violates the direction, crosses branches, references a
sibling, or picks up any `Microsoft.Extensions.*` dependency.

Each protocol lives in its own project so protocols can expand — and new
identity protocols can be added — without touching the shared base or the
other protocols, and so each protocol is built and tested in isolation.

### Ownership boundaries

- **Root (`…IdentityModel`)** owns: the canonical identity domain model
  (subjects, application identities, actors, credentials, claims and
  attributes, sessions, authentication context, authentication results); the
  `IdentityModelException` error root; and the shared internal
  descriptor-materialization helper (`ModelSnapshot`), exposed to the family
  via `InternalsVisibleTo`.
- **`…IdentityModel.Protocols`** owns: the shared, transport-agnostic protocol
  abstractions — party roles, published-entity metadata, message envelopes,
  response status, validation results, logout semantics, binding and endpoint
  descriptors — plus the cross-protocol validation code vocabulary. Every
  protocol contract branch derives from these.
- **`…Protocols.OpenIdConnect`** owns: the OpenID Connect contract surface —
  discovery/client metadata, authorization/token/ID token/UserInfo/logout
  contracts, OIDC constants and validation codes.
- **`…Protocols.Saml`** owns: the SAML 2.0 contract surface — assertion
  content, protocol messages, entity metadata, bindings, SAML constants.
- **`…IdentityModel.Token`** owns: the protocol-neutral token/assertion
  normalization contracts — what it means to be "a token" (issuer, subject,
  audiences, temporal validity, authentication context, normalized claims,
  provenance) independent of wire format, expressed **in the root canonical
  types** (`SubjectIdentifier`, `IIdentityClaimCollection`,
  `AuthenticationContext`) — plus the one genuinely protocol-neutral
  behavior, a `Validate()` data-rule method on the token base. It ships no
  neutral reader or writer seam (parsing and serialization are inherently
  format-specific; see the Token normalization decisions).
- **`…Token.JsonWebToken`** owns: concrete JWT document fidelity — JOSE
  header shape, compact serialization, registered and OIDC ID-token claims,
  JWT-specific validation descriptors and negative-case behavior.
- **`…Token.Saml`** owns: concrete SAML assertion token fidelity —
  statements, conditions, subject confirmation, NameID semantics, raw
  assertion context.

Two placement rules resolve every "where does this type go?" question:

1. If a concept is a *protocol contract* (message shape, metadata shape,
   validation semantics), it lives in that protocol's branch project — or, if
   it is genuinely cross-protocol, in the shared `…Protocols` base.
2. If a concept is *concrete document behavior* for one token format, it
   lives in that format's token package.

### Adding a protocol package

A new identity protocol (say, WS-Federation or a future protocol) is a new
`…IdentityModel.Protocols.<Name>` project:

1. Create `libraries/IdentityModel/Assimalign.Cohesion.IdentityModel.Protocols.<Name>/`
   with `src/` and `tests/`, referencing `Assimalign.Cohesion.IdentityModel.Protocols`.
2. Add `<InternalsVisibleTo Include="…Protocols.<Name>" />` to the root project
   (for the shared `ModelSnapshot`) and to `…Protocols` (for shared endpoint
   validation) — the two shared-internal seams the branches use.
3. Register the assembly in `frameworks/Assimalign.Cohesion.App.props`, add it
   to both `.slnx` files, and add a branch entry to
   `IdentityModelFamilyBoundaryTests` / `IdentityModelNamespaceAlignmentTests`.
4. Add `docs/OVERVIEW.md` + `docs/DESIGN.md`; the deep family-level rationale
   stays in this keystone document, which each project's `DESIGN.md` links to.

Implementation packages that *execute* — protocol readers/writers, metadata
retrievers, validators that run cryptography — are separate descendant projects
again (for example `…Protocols.OpenIdConnect.Metadata` retrieving discovery
documents over HTTP, or `…Protocols.Saml.Serialization` reading assertion XML).
The contract branch stays pure data; the executable layer depends on it and on
the Security/transport areas.

## Namespace map

Each project's assembly name equals its root namespace (the repo rule), so the
project boundary and the namespace boundary coincide:

| Assembly / namespace | Contents |
|---|---|
| `Assimalign.Cohesion.IdentityModel` | Canonical identity domain model: subjects, application identity, actors, credentials, claims/attributes, sessions, authentication context and results; the `IdentityModelException` error root. |
| `Assimalign.Cohesion.IdentityModel.Protocols` | Cross-protocol abstractions: protocol parties and roles, protocol metadata, request/response envelopes, validation results and codes, logout semantics, binding and endpoint descriptors. |
| `Assimalign.Cohesion.IdentityModel.Protocols.OpenIdConnect` | OpenID Connect contracts: issuer/discovery metadata, client metadata and registration shape, authorization/token/ID token/UserInfo/logout message contracts, OIDC constants. |
| `Assimalign.Cohesion.IdentityModel.Protocols.Saml` | SAML 2.0 contracts: assertion content (subject, NameID, statements, conditions, confirmations), protocol messages (AuthnRequest, Response, Status, logout), entity metadata, binding descriptors, SAML constants. |

Within each project, `src/` uses the repo's standard `Abstractions/`,
`Extensions/`, `Internal/`, and `Exceptions/` folders where applicable. The
`IdentityModelNamespaceAlignmentTests` guardrail asserts every public type in
every family assembly lives under its assembly-name namespace.

## Modeling conventions

- **Interface-first public surface, with a deliberate carve-out.** Contracts
  where a second implementation is plausible are interfaces
  (`IIdentitySubject`, `IIdentityClaim`, `IIdentityClaimCollection` — lazy
  token-backed implementations are realistic for the token packages).
  Data-shaped types with exactly one meaningful shape ship as **sealed
  immutable classes with no interface** (`AuthenticationResult`,
  `AuthenticationContext`, `AuthenticationSession`, `AuthenticationFailure`,
  `IdentityCredential`, `IdentityAttribute`): an interface there would make
  every future member addition a breaking change for implementers, while a
  sealed class grows additively. This is the growth policy for the family —
  new members land on sealed classes freely; interface members are
  one-way doors and get scrutinized accordingly.
- **Descriptor → immutable model.** Non-trivial models follow the existing
  token pattern: a mutable descriptor for construction, an immutable model
  that snapshots the descriptor. Mutation after construction never leaks into
  a materialized model — including nested content (`IdentityClaimValue`
  factories defensively copy arrays, objects, and binary content, which also
  makes cyclic value graphs unconstructible). Materialization invariant
  violations throw `IdentityModelException`; malformed direct arguments throw
  the standard `Argument*` exceptions.
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
- **Naming.** Family-owned nouns that would collide with neighboring areas
  carry the `Identity` prefix (`IdentityCredential`, not `Credential` — the
  Security area is the natural next owner of a credential-shaped type).
  `Authentication*` and `SubjectIdentifier` are unambiguous domain terms and
  stay unprefixed.

## Canonical domain model decisions

Feature `[L01.01.12.02]` replaced the thin identity markers with the canonical
model. The decisions below were stress-tested against OIDC Core / RFC 8693 /
SAML Core semantics before implementation and are load-bearing; changing any
of them is a breaking design change, not a refactor.

- **Canonical claim vocabulary = IANA JWT short names.** `IdentityClaimTypes`
  uses the IANA-registered names (`sub`, `email`, `given_name`, `roles`, …) —
  the only registry-governed cross-vendor vocabulary. SAML attribute names
  map onto them during normalization, with originals preserved in
  `IdentityClaimProvenance` (`OriginalType`, `OriginalNameFormat`,
  `OriginalValueType`, `OriginalFriendlyName` are distinct fields because
  real attributes carry them simultaneously). `cnf` is the canonical landing
  spot for proof-of-possession bindings (OIDC `cnf` / SAML holder-of-key).
- **Multi-value data canonically = duplicate claims.** One claim type, many
  claims. `IdentityValueKind.Array` is reserved for genuinely structured
  single values (for example the OIDC `address` object). Because wire data
  arrives in both shapes, `IIdentityClaimCollection.GetValues` flattens
  array-valued claims one level so consumers cannot tell the shapes apart —
  the fidelity tests pin this. `IdentityAttribute` is a standalone carrier
  constructed explicitly by protocol branches; the collection deliberately
  has no lossy claims→attributes grouping (it would merge cross-issuer
  provenance).
- **Both `Double` and `Decimal` value kinds exist from day one.** JSON claim
  numbers live in IEEE-754 double's domain (a valid JWT can carry `1e300`,
  which overflows `decimal`); SAML `xs:decimal` requires exactness double
  cannot give. `IdentityValueKind` ordinals are pinned by test because
  consumers switch exhaustively over them. Normalization rule: integral wire
  numbers → `Integer`, fractional JSON numbers → `Double`, exact decimal wire
  values → `Decimal`; `DateTime` is reserved for source-typed dates, never
  numeric epoch timestamps.
- **`SubjectIdentifier` equality = (Value, Format, Issuer,
  RelyingPartyQualifier), ordinal.** This is the SAML Core §8.3 matching
  scope for persistent identifiers and covers OIDC pairwise sector scoping;
  omitting the relying-party qualifier would conflate per-SP pseudonyms — a
  cross-relying-party identity collision. `Issuer` carries the SAML
  `NameQualifier` (defaulting to the assertion issuer) / OIDC `iss`;
  `SPProvidedID` is spec-defined as non-matching and rides in `Properties`,
  which never participates in equality. Absent formats normalize to the
  `Unspecified` constant at construction so matching is normalization-path
  independent.
- **One sealed `IdentitySubject`; kind is data.** `IdentityKind` gained
  `Unknown = 0` (the honest default): RFC 8693 `act` claims and SAML
  delegation entries identify actors without declaring user-vs-application,
  so normalizers must never fabricate a kind. Per-kind subject classes were
  rejected — they would make every future `IdentityKind` member a new public
  type and break generic materialization in the token-normalization feature.
- **Actor chains are single recursive pointers with pinned direction.**
  `Actor` is the current acting party, acting on behalf of the subject that
  carries it; `Actor.Actor` is the *prior* actor — the party that previously
  acted for the same subject before delegation passed on (RFC 8693 nested
  `act` history-trail semantics: deepest = least recent, informational).
  Chains are depth-capped at materialization
  (`IdentitySubject.MaxActorDepth`), which also terminates walks over cyclic
  third-party `IIdentitySubject` implementations *as seen at construction*.
  The snapshot guarantee is scoped honestly: descriptor collections and
  family-owned types are copied, but interface-typed inputs (a foreign
  `IIdentitySubject` actor, a foreign `IIdentityClaim`) own their own
  immutability — a hostile implementation that mutates after materialization
  is its author's defect, and consumers walking untrusted chains should bound
  their walks with the same depth cap. Per-link delegation metadata (SAML
  `DelegationInstant`, confirmation method) is represented as claims on the
  acting subject with provenance.
- **Sessions correlate; they do not own the principal.**
  `AuthenticationSession` holds the subject's `SubjectIdentifier` + kind, not
  the full subject graph — session stores persist and rehydrate sessions, and
  snapshotted claims would go stale. Single logout is the shaping scenario:
  a session carries `Protocol`, `Issuer`, and **plural** `ProviderSessionIds`
  (SAML allows several `SessionIndex` values per session; OIDC `sid` is the
  single-element case), because provider session ids are only unique per
  issuer.
- **`AuthenticationResult` is the platform seam, so it is audit-complete.**
  It carries provenance (`Protocol`, `Issuer`, `Audience`), evidence linkage
  (`EvidenceId` = JWT `jti` / SAML assertion id as a string — never a token
  type, which would invert the family dependency), credential linkage
  (`CredentialId`), and on failures the `AttemptedSubject` plus the original
  wire error (`AuthenticationFailure.OriginalCode`, `ErrorUri`). The
  success/failure invariant (exactly one of `Subject`/`Failure`) is enforced
  at materialization and surfaced to nullable flow analysis via
  `[MemberNotNullWhen]`. Provider session data flows through
  `AuthenticationContext` (`ProviderSessionIds`, `SessionExpiresAt`,
  `AuthenticatingAuthorities`) so IdentityHub can build the session from the
  result without side channels.
- **Expiry is computed, never stored — and states fail closed.** Neither
  `IdentityCredentialState` nor `AuthenticationSessionState` has an
  `Expired` member; `IsUsable(at)` / `IsActive(at)` combine administrative
  state with the temporal window, so there is a single source of truth.
  Both enums pin `Unknown = 0` as never-usable/never-active — a forgotten
  state assignment (or a store rehydration that fails to map state) cannot
  produce a live credential or session. Temporal cross-field guards differ
  deliberately: a credential's `NotBefore`/`ExpiresAt` come from one source
  (the credential itself), so a backwards window is a data invariant and
  materialization rejects it; a session's `ExpiresAt` may be wire-sourced
  from the *provider's* clock (SAML `SessionNotOnOrAfter`) while `CreatedAt`
  is local, so an at-or-before-creation expiry is legal, materializes
  normally, and is simply never active.
- **`AuthenticationProtocol` is an open vocabulary struct**, not an enum:
  new protocols must not break the family. Names normalize to trimmed
  lowercase at construction; `default` is a fully-functional `Unknown` whose
  `Name` is never null. Well-known values: `oidc`, `oauth2`
  (client-credentials and other OAuth-only events), `saml2`.
- **Claim lookup vocabulary is `Contains` / `TryGet` / `GetAll` /
  `GetValues`.** The token package's `TryGetClaim`/`GetClaims` members adopt
  this vocabulary verbatim when feature `[L01.01.12.06]` aligns it to the
  root model, so the family ships one spelling for "find claims by type".
  Convenience accessors (`GetString`, `HasClaim`) are `extension(…)` members,
  not interface members.
- **Aggregated/distributed OIDC claims.** `IIdentityClaim.Issuer` is the
  canonical carrier for *resolved* third-party-asserted claims. Unresolved
  `_claim_names`/`_claim_sources` references never enter the canonical
  collection as claims; the OIDC branch owns their representation.
- **Properties bags are `IReadOnlyDictionary<string, IdentityClaimValue>`**
  on models (`AuthenticationResult`, `AuthenticationSession`,
  `AuthenticationContext`, `IdentityCredential`) — the typed union avoids
  string-smuggling structured data. `SubjectIdentifier.Properties` is
  deliberately `string→string`: it carries wire qualifier leftovers only and
  keeps identifier equality trivially cheap.

## Shared protocol abstraction decisions

Feature `[L01.01.12.03]` created the `Assimalign.Cohesion.IdentityModel.Protocols`
project: the cross-protocol concepts the OIDC and SAML branch projects derive
from. These decisions were stress-tested against both branches' forward
requirements before implementation and are load-bearing.

- **Abstract envelope bases are data-only, with protocol pinned by the
  derived type.** `ProtocolMetadata` and `ProtocolMessage` (and the
  request/response/logout derivatives) are guided abstract bases: get-only
  properties snapshotted in a `protected` constructor, no virtual or abstract
  behavior members, so derivatives can never change base semantics. The
  `AuthenticationProtocol` is a constructor argument supplied by the derived
  type — never descriptor data — so an object can never claim a protocol that
  contradicts its type. Their descriptors are abstract too; branch types pair
  with branch descriptors. Growth flows through descriptors, not positional
  constructor parameters.
- **`ProtocolMetadata` models *published* entity metadata and `EntityId` is
  always required.** Shapes without an identifier — an OIDC dynamic client
  registration *request*, where the server assigns `client_id` in the
  response — are branch-owned request types and must not derive from the
  base. Endpoints and keys are flat lists carrying an optional
  `ProtocolRole` scope (null = entity-wide) so dual-role SAML entities keep
  key/endpoint role attribution without a SAML-shaped role-descriptor
  hierarchy in the base; role-grouped views are branch projections.
  `RawDocument` preserves the as-received document for later signature
  re-verification, mirroring `ProtocolMessage.RawMessage`. `CacheDuration`
  is a computed `TimeSpan` — SAML `xs:duration` calendar components are not
  exactly representable, so branches preserve the lexical form in
  `Properties` when inexact.
- **Locations and destinations are wire-exact strings, never `System.Uri`.**
  `Uri` normalizes (default-port dropping, host case folding, IDN encoding)
  and its equality is loose — but endpoint/destination comparison is a
  signed-value security control in SAML and an exact-match rule for OAuth
  redirect URIs. `ProtocolEndpoint.Location`/`ResponseLocation` are validated
  to parse as absolute URIs at materialization (validate with `Uri`, store
  the string); `ProtocolMessage.Destination` is unvalidated wire capture —
  judging it is a validator's job. All comparisons are ordinal.
- **Correlation semantics are pinned once.** `CorrelationState` carries the
  opaque round-trip value on both legs (OIDC `state`, SAML `RelayState`);
  `InResponseTo` is message-*identifier* correlation only (SAML) and is
  always null for OIDC, which has no message ids. `Issuer` on a message is
  the *sender*. `Reason` on a logout request is strictly the *why* category
  (SAML `Reason` URI) — subject hints like OIDC `logout_hint` are *who* and
  live on branch types.
- **`ProtocolResponse.Status` is required and has no default.**
  Absence-means-success is a wire-parsing rule for branch materializers; a
  response whose status was never mapped fails construction rather than
  reading as accepted. `ProtocolResponseStatus` stores `Succeeded`
  orthogonally to wire codes (SAML successes always carry the `Success` code;
  single logout can succeed with a `PartialLogout` sub-code) and carries the
  ordered `SubCodes` chain because SAML status nesting is unbounded.
- **The `ProtocolBinding` vocabulary is transport-shaped, one name per wire
  shape**: `http-redirect`, `http-post`, `http-fragment`, `http-artifact`,
  `soap`, `back-channel`. OIDC response modes map onto these
  (`query`→`http-redirect`, `form_post`→`http-post`,
  `fragment`→`http-fragment`) with the wire spellings as branch constants —
  two spellings for one shape in an open vocabulary would make stored
  metadata and routing silently mismatch. A binding names the transport
  shape only; message encoding differs per protocol, so dispatch is always
  on the (protocol, binding) pair. `ProtocolEndpointKind` is the same
  open-vocabulary shape with no root well-known values: endpoint kinds are
  protocol-spec vocabulary and live as typed values in the owning branch.
- **`ProtocolExchange` describes two legs** (request endpoint + optional
  response endpoint, each leg's binding on its endpoint; null response
  endpoint = back-channel same-connection). Both protocols' flagship
  front-channel flows terminate the two legs at different parties'
  endpoints; a SAML artifact flow is two exchanges.
- **`ProtocolKeyUse.Unspecified = 0` is deliberately not the fail-closed
  `Unknown`.** Both SAML and JWK define an absent `use` as valid-for-any-
  purpose; the descriptive model preserves that wire semantic, and the
  fail-closed rule applies to *usability gates*, not wire data. Consumers
  use `CanSign`/`CanEncrypt` (which treat `Unspecified` as unrestricted)
  instead of naive equality filters; whether an unrestricted key is trusted
  is downstream validator policy. Keys carry multiple `Algorithms` (SAML
  permits several `EncryptionMethod` entries).
- **Validation diagnostics are the family's shared currency.**
  `ProtocolValidationResult` is sealed; `Succeeded` is computed (no
  error-severity diagnostics) so "succeeded with errors" is
  unconstructible; `ProtocolValidationSeverity.Error = 0` so a defaulted
  severity reads as the most severe interpretation. Richer results within
  this branch *compose* the result or reuse `ProtocolValidationDiagnostic` —
  no result inheritance hierarchy. The **token branch cannot share this
  currency**: branch independence (enforced by the boundary tests) forbids
  `…Token` from referencing `…Protocols`, so `[L01.01.12.06]` defines a
  branch-local `TokenValidationResult`/`TokenValidationDiagnostic`/
  `TokenValidationSeverity` that *mirror* this contract (sealed, computed
  `Succeeded`, `Error = 0`, accumulate-then-materialize) rather than
  reference it. Two parallel copies is the deliberate cost of keeping the
  branches independent; promoting the type to the root was rejected because
  validation vocabulary is a protocol/token concern, not canonical-identity,
  and the anchor must reference no other assembly. Association with the
  validated artifact is the caller's composition concern. No builder:
  validators accumulate a `List<>` and materialize once.
- **`ProtocolParty` is a minimal reference** (trust-registry entries) with
  equality over (Identifier, Role) ordinal — `DisplayName`/`Properties`
  excluded, per the `SubjectIdentifier` precedent — and deliberately no
  protocol member: one entity identifier can serve both protocols; protocol
  provenance lives on metadata and messages.

## OpenID Connect branch decisions

Feature `[L01.01.12.04]` created the
`Assimalign.Cohesion.IdentityModel.Protocols.OpenIdConnect` project. The branch
was stress-tested against Discovery 1.0 / DCR 1.0 / Core 1.0 / RP-Initiated
Logout 1.0 / Back-Channel Logout 1.0 / RFC 7636 / RFC 9207 requirements and
against the JWT-package and cross-protocol features that build on it.

- **Guards stop at artifact identity; conformance is diagnostics.** The only
  branch hard guards are the members that make a *constructed request*
  recognizable as its artifact (authorization request: `client_id` +
  `response_type`; token request: `grant_type`) plus the base's settled
  guards (metadata `EntityId`, response `Status`). Everything RECEIVED from
  a counterparty — success artifacts, `token_type`, the ID token's required
  five, UserInfo's `sub` — is nullable and reported by `Validate()`:
  received documents are exactly what compliance suites must hold, and
  non-nullable members backed by guards are one-way nullability doors on
  sealed types.
- **`Validate()` methods are pure data rules** — functions of (artifact,
  explicit options) with no I/O, no clock ownership (`ValidateAt` is a
  required option), and no cryptography. The Core §3.1.3.7 split with the
  JWT package: this branch owns the data checks (issuer/audience/azp with
  the MUST-vs-SHOULD severity map, temporal windows, nonce, `auth_time`
  age); the JWT package owns signature and `at_hash`/`c_hash`
  presence-plus-value as one crypto concern. Additional audiences are
  untrusted by default (the spec's posture) with an explicit
  `AllowAdditionalAudiences` opt-out, on both the ID token and the logout
  token; an absent RFC 9207 `iss` from an advertising provider is an Error
  (the spec's MUST-reject). Skew arithmetic always applies to the
  caller-controlled instant so extreme wire timestamps diagnose instead of
  throwing. Cross-protocol diagnostic codes live in the shared `…Protocols`
  project's `ProtocolValidationCodes`; OIDC-minted codes in
  `OpenIdConnectValidationCodes`.
- **Envelope aliasing, not duplication.** `ClientId` on requests and the
  RFC 9207 `iss` on authorization responses are aliases of the base
  `ProtocolMessage.Issuer` (the sender), exactly as F3's descriptor docs
  pin — materializers set them only from wire data so RFC 9207 absence
  detection works, and `authorization_response_iss_parameter_supported`
  rides provider metadata to drive it.
- **Metadata: typed members are stored wire truth; the base endpoint list is
  the well-formed projection.** Materialization projects valid typed
  endpoint values into the base list with branch kinds and bindings;
  malformed values stay typed-only and `Validate()` reports them; the
  inherited descriptor list carries extension endpoints (revocation,
  introspection, …) with kind-collision guards. Client metadata projects
  its logout and JWK Set URIs (SLO fan-out enumerates the neutral list);
  redirect URIs are deliberately not endpoints. The registered client
  record derives from `ProtocolMetadata` (`EntityId` = `client_id`); the
  registration *request* is the standalone
  `OpenIdConnectClientRegistrationRequest` reusing the client descriptor
  with the identifier required absent.
- **Token contracts are single-source.** `OpenIdConnectIdToken` /
  `OpenIdConnectLogoutToken` / `OpenIdConnectUserInfo` build their claim
  collections from the typed members plus extension claims at
  materialization, stamping OIDC provenance; extension claims colliding
  with a typed member are rejected, so the validated surface and the
  claims the cross-protocol mapper reads cannot disagree. NumericDate
  claims keep their wire shape (integer seconds) in the collection while
  typed members expose `DateTimeOffset`. `RawToken`/`RawDocument` retain
  originals (the RP-initiated logout `id_token_hint` and signature
  re-verification need them); `JwtId` feeds
  `AuthenticationResult.EvidenceId`.
- **The subject-identifier recipe is pinned once** in
  `OpenIdConnectSubjectExtensions.GetSubjectIdentifier()`: value = `sub`,
  issuer = `iss`, unspecified format, no relying-party qualifier — wire
  fields only, used identically by the login leg and the back-channel
  logout leg so single-logout equality cannot fork. Sector enrichment is a
  consumer policy applied on both legs or neither. Subjects on token
  contracts are raw wire strings for the same reason.
- **Back-channel logout composes with the shared logout semantics**:
  `OpenIdConnectBackChannelLogoutRequestDescriptor.Apply(token)` populates
  the base issuer/subject/session members from the parsed logout token
  (the base envelope snapshots the descriptor, so population precedes
  materialization), and materialization guards agreement between the
  descriptor and the token — internal consistency is structural. A
  sub-only logout token maps onto the family's "empty session ids +
  subject = all sessions" pin. `Events` is a dictionary (event URI →
  payload) because the wire shape is a JSON object and §2.6's
  payload-is-an-object check must be expressible.
- **`response_type` is a single wire-exact string with computed parts and a
  set-comparison helper** (`OpenIdConnectResponseTypes.Matches`): the OAuth
  Multiple Response Types registry treats the combination as the unit but
  makes it order-insensitive, so only atomic constants ship and no ordinal
  comparison is ever correct. Every other space-delimited wire list
  (scopes, prompts, `acr_values`, `amr`, `ui_locales`) is modeled as a
  list, with the join/split as a transport concern.
- **Unresolved aggregated/distributed claims** ride
  `OpenIdConnectClaimsSource` entries on the ID token and UserInfo —
  never the claim collection, per the canonical-model pin. Resolution
  (HTTP, JWT parsing) stays out of scope; the mapping feature consumes
  resolved claims via `IIdentityClaim.Issuer`.
- **Scope notes**: every F4 type pins `AuthenticationProtocol.OpenIdConnect`
  (a type named OpenIdConnect\* must never claim another protocol; pure
  OAuth-only flows are representable as data but read `oidc` — a future
  OAuth branch would get its own types, and `oauth2` remains the
  `AuthenticationResult`-level provenance value). Client secrets are never
  modeled (client assertions are — they're signed public artifacts);
  JAR/`claims` parameters are carried as raw wire strings without object
  models.

## SAML branch decisions

Feature `[L01.01.12.05]` created the
`Assimalign.Cohesion.IdentityModel.Protocols.Saml` project. The branch was
stress-tested against SAML 2.0 Core, Bindings, Profiles (Web Browser SSO,
Single Logout), and Metadata before implementation, and against the SAML
token-package and cross-protocol features that build on it. It applies the same
guards-stop-at-artifact-identity and validation-is-diagnostics discipline the
OIDC branch settled.

- **The assertion is the branch's token contract, and its `Validate()` owns
  pure SAML data rules.** `SamlAssertion` is the SAML counterpart to the OIDC
  ID token: it materializes from a descriptor, builds its claim collection
  once, and validates issuer / subject presence / conditions window / audience
  restrictions / bearer confirmation / authn-context presence as functions of
  (assertion, explicit options). Signature verification and `EncryptedID` /
  `EncryptedAssertion` / `EncryptedAttribute` decryption are deliberately absent
  — they are the SAML token package's one crypto concern, so each SAML Core
  rule has exactly one owner. The only hard guards are the artifact-identity
  members (`SamlAssertion.Id`, message `MessageId`, metadata `EntityId`); every
  wire-received field is nullable and reported by `Validate()`, because received
  documents are exactly what compliance suites must hold.
- **Real SAML structure is preserved, never flattened.** The stress-test
  rejected a naive flat assertion. The branch models `SamlSubject`,
  `SamlSubjectConfirmation`, `SamlSubjectConfirmationData`, `SamlAuthnContext`,
  `SamlConditions`, and `SamlAuthnStatement` as distinct value objects; the
  `Issuer` is a `SamlNameId` (SAML Core types `Issuer` as a NameID with format
  and qualifiers), not a bare string. Content the descriptive layer cannot open
  is a typed `SamlEncryptedElement` marker (never silently dropped), and each
  signable element retains its own verbatim `RawXml` (the `<saml:Assertion>`
  subtree separately from the `<samlp:Response>` element) so the token package
  can re-verify an assertion-level signature independently of a response-level
  one.
- **Audience satisfaction is AND-across / OR-within, and fails closed.** An
  assertion satisfies its audience constraint when the relying party matches at
  least one audience of *every* `AudienceRestriction`. `SamlConditions.
  IsAudienceSatisfied` implements exactly that; an empty restriction set (a
  wire malformation) is treated as unsatisfiable, never vacuously true. The
  audience check runs only when the caller supplies `ExpectedAudience` and the
  assertion actually carries restrictions — an unrestricted assertion is
  unrestricted.
- **Bearer confirmation is existential and profile-shaped.** The Web Browser
  SSO profile is met when at least one `SubjectConfirmation` is `bearer` and its
  data has `NotOnOrAfter` present and in the future, no `NotBefore`, a
  `Recipient` matching the expected assertion-consumer URL when one is
  expected, and an `InResponseTo` equal to the sent request id (both-null is
  the legal identity-provider-initiated case). `RequireBearerConfirmation`
  defaults true; a holder-of-key-only subject fails the profile. The profile's
  authentication-statement requirement (Profiles §4.1.4.2) is enforced the same
  way: `RequireAuthnStatement` defaults true, so an assertion with zero
  authentication statements fails closed rather than being accepted with no
  asserted authentication context — the attribute-only assertions SAML Core
  §2.7.2 permits opt out by setting it false.
- **Clock skew is applied to the caller instant, once.** Every temporal rule
  (conditions window, bearer `NotOnOrAfter`) evaluates against
  `options.ValidateAt ± options.ClockSkew`, never per field, so a single skew
  source governs the whole assertion and extreme wire timestamps diagnose
  rather than throw — the same discipline as the OIDC branch. `ClockSkew`
  defaults to five minutes; the library never owns a clock (`ValidateAt` is
  required).
- **Claims keep raw SAML names; authentication data does not become claims.**
  `BuildClaims` emits `sub` from the subject NameID and one claim per attribute
  *value*, keyed by the **raw** SAML attribute name (the cross-protocol mapper
  remaps to canonical types later), with provenance carrying the protocol
  (`saml2`), original name, name format, and friendly name so nothing is lost;
  multi-value and repeated attributes become duplicate claims, per the family's
  canonical multi-value rule. Authentication-statement data (authn instant,
  context class, session index) is deliberately *not* forced into the claim
  collection — it flows to session and authorization layers through the typed
  `SamlAuthnStatement`, kept SAML-native.
- **The NameID-to-`SubjectIdentifier` recipe is pinned once, and both SSO legs
  use it.** `SamlSubjectExtensions.GetSubjectIdentifier()` maps value = NameID
  value, format = NameID `Format` (normalized to unspecified when absent),
  issuer = `NameQualifier` or the message/assertion issuer fallback (SAML Core
  §2.2.3), relying-party qualifier = `SPNameQualifier`, with `SPProvidedID`
  riding `Properties` (non-matching, so excluded from equality). The login leg
  (assertion subject) and the logout leg (logout `NameID`) both derive
  identifiers through this one recipe, so single-logout correlation cannot fork
  — the SAML Core §3.7.3.2 obligation that the logout NameID be consistent with
  the login NameID is what makes the two legs converge, and identical field
  mapping is what makes the derived identifiers *equal*.
- **Single logout composes the shared logout semantics.**
  `SamlLogoutRequestDescriptor.Apply(nameId, sessionIndexes)` populates the base
  `Subject` (through the pinned recipe, defaulting the name qualifier to the
  message `Issuer`) and the base `ProviderSessionIds` (the `SessionIndex`
  values), so correlation joins on the `(Issuer, ProviderSessionIds)` pair
  *identically* to OIDC back-channel logout, and an empty session set means
  every session for the subject. The partial-logout outcome is a success status
  carrying a `PartialLogout` sub-code (`ProtocolResponseStatus.SuccessWith`),
  never a failure.
- **Response validation is envelope-scoped; assertions validate themselves.**
  `SamlResponse.Validate` checks the success status, the `InResponseTo` and
  `Destination` matches, and — on success — the presence of at least one
  assertion (cleartext or encrypted). Each contained assertion's data rules run
  through `SamlAssertion.Validate`, so the envelope and its assertions have
  separate, composable validators. A SAML success always carries the `Success`
  status code on the wire (`Status.Succeeded` is stored, not inferred).
- **Entity metadata: role descriptors are authoritative; the flat lists are a
  role-stamped projection.** `SamlEntityMetadata` derives `ProtocolMetadata`;
  materialization projects each `SamlRoleDescriptor`'s endpoints and keys into
  the inherited flat lists and **stamps `ProtocolEndpoint.Role` /
  `ProtocolKey.Role` from the enclosing role descriptor**, overwriting whatever
  the caller authored, so a role-scoped entity never surfaces a null-role entry
  and a dual-role entity (identity provider *and* service provider) keeps its
  per-role keys and endpoints distinguishable to protocol-neutral consumers.
  The typed policy the base cannot hold (NameID formats, protocol support
  enumeration, signing flags) stays on the role descriptor. Conformance
  (at least one role descriptor, SAML 2.0 protocol support, `validUntil`
  expiry) is `Validate()` diagnostics, not materialization guards.
- **Bindings and NameID formats map through single sources.** SAML binding
  URIs live in this branch; `SamlBindings.ToProtocolBinding` is the one place
  they map onto the transport-shaped `ProtocolBinding` vocabulary (SOAP and
  PAOS both collapse to `soap`), mirroring how the OIDC branch maps response
  modes. `SamlNameIdFormats` forwards to the root `SubjectIdentifierFormats`
  constants — one literal per URI, because the canonical `SubjectIdentifier.
  Format` participates in equality and a second copy would be a drift hazard.
- **Scope notes**: every SAML type pins `AuthenticationProtocol.Saml2`. Status
  codes are the exact OASIS URNs, consumed through `Status.Code` (top-level)
  and `Status.SubCodes` (nested, unbounded). XML serialization is out of scope:
  raw assertion/response XML is preserved as opaque context for the SAML token
  package, which owns reading and writing it.

## Token normalization decisions

Feature `[L01.01.12.06]` evolved `Assimalign.Cohesion.IdentityModel.Token` from
a self-contained model with its own parallel abstractions into the
protocol-neutral normalization layer, expressed in the root canonical types.
The decisions below were stress-tested against the OIDC ID token and SAML
assertion shapes and against the JWT/SAML token packages that build on it.

- **The token layer reuses the root canonical types; it no longer duplicates
  them.** The old branch carried its own `IIdentityTokenClaim` /
  `IdentityTokenClaim` / `IdentityTokenValueKind` and `object?` value bags.
  Those are deleted. A normalized token now exposes `SubjectIdentifier?
  Subject`, `IIdentityClaimCollection Claims`, `AuthenticationContext?
  AuthenticationContext`, `AuthenticationProtocol Protocol`, and
  `IReadOnlyDictionary<string, IdentityClaimValue> Properties` — the same
  types every other family member speaks. This is what makes a JWT-normalized
  and a SAML-normalized principal comparable, and it removes the AOT-hostile
  `object?` surface from the neutral layer. Claim lookup is the root
  `Contains`/`TryGet`/`GetAll`/`GetValues` on `Claims`, so the family ships
  one spelling; `HasAudience` and the temporal predicates (`IsActive`,
  `IsExpired`) are `extension(IIdentityToken)` members, not interface members.
- **`Claims` is the authoritative record; the typed members are its
  projection.** The complete normalized claim set lives in `Claims` with
  provenance. The first-class members (`Subject`, `Issuer`, `Audiences`, the
  temporal window, `AuthenticationContext`) are the convenience/normalized
  projection a concrete token package populates *consistently with* `Claims`.
  This mirrors how the OIDC ID token already materializes its claim collection
  from typed members; keeping one authoritative surface is what lets a
  compliance test assert a JWT and a SAML assertion resolve to the same
  canonical shape.
- **Two issuers, pinned.** `IIdentityToken.Issuer` is the *asserting* issuer
  (OIDC `iss` / SAML `Assertion/Issuer`) — the answer to "who issued this
  token". `Subject.Issuer` is the subject-*scoping* qualifier (SAML
  `NameQualifier`, defaulting to the assertion issuer) and MAY differ. Identity
  equality/lookup uses `SubjectIdentifier`; issuer trust uses
  `IIdentityToken.Issuer`. Concrete packages set them independently.
- **The document format and the protocol are distinct axes.**
  `IdentityTokenKind` (`Unknown = 0` fail-closed, `JsonWebToken`, `Saml`) is
  the wire shape, pinned by the concrete type through the constructor.
  `AuthenticationProtocol` is provenance carried as descriptor data, because a
  JWT may be an OIDC ID token (`oidc`) or an OAuth access token (`oauth2`)
  while a SAML assertion is always `saml2`.
- **Validation is a `Validate(options)` method on the token base, not a
  seam.** Every validator in the family is a data-rule method on the artifact
  (`OpenIdConnectIdToken.Validate`, `SamlAssertion.Validate`); there is no
  `IValidator` anywhere. The token layer follows suit: the neutral checks
  (issuer match, audience membership, the primary temporal window with skew
  applied to the caller instant) are a method on `IdentityToken`. An
  `IIdentityTokenValidator` interface + default implementation was rejected as
  a seam-for-its-own-sake with no second implementation. Format-specific rules
  (signature, a SAML bearer-confirmation window, per-restriction audiences)
  belong to the concrete package.
- **No neutral reader or writer seam.** Parsing (compact JWT vs assertion XML)
  and serialization are inherently format-specific with no protocol-neutral
  behavior and no polymorphic caller that holds the abstraction without knowing
  the format, so `[L01.01.12.06]` ships neither. Concrete readers/writers that
  return their concrete token types are the JWT/SAML packages' concern; a
  neutral entry point is introduced only if a real multi-format dispatch need
  appears.
- **`TokenValidationResult` is a branch-local mirror.** See the shared-currency
  decision above: branch independence forbids the token branch from referencing
  `…Protocols`, so the token validation result/diagnostic/severity mirror the
  protocol contract as independent types.
- **Scope of the AOT purge.** `[L01.01.12.06]` removes `object?` from the
  neutral token base only. The JWT JOSE-header dictionary
  (`IJsonWebToken.Header`) and the SAML condition dictionary
  (`ISamlToken.Conditions`) remain `object?` and are deliberately deferred to
  `[L01.01.12.07]` (#608) and `[L01.01.12.08]` (#612), which take those
  packages to grade and map those surfaces onto `IdentityClaimValue` then.

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
  generation. Value-kind handling is `IdentityClaimValue`'s trim-safe type
  pattern matching, never runtime reflection.
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
| `[L01.01.12.03]` #592 | Shared protocol abstractions — the `…Protocols` project. |
| `[L01.01.12.04]` #596 | OpenID Connect contract branch — the `…Protocols.OpenIdConnect` project. |
| `[L01.01.12.05]` #600 | SAML 2.0 contract branch — the `…Protocols.Saml` project. |
| `[L01.01.12.06]` #604 | Token normalization alignment with the root model. |
| `[L01.01.12.07]` #608 | JWT package to OIDC grade. |
| `[L01.01.12.08]` #612 | SAML token package to assertion grade. |
| `[L01.01.12.09]` #616 | Cross-protocol claim mapping, migration fixtures, compliance matrices, NativeAOT evidence, and the family's `docs/Assembly/` API reference pages (deferred from earlier features so the reference is written once against the settled surface). |
