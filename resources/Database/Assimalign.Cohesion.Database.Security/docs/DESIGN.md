# Assimalign.Cohesion.Database.Security — Design

Security contracts shared by all five models (area architecture:
[resources/Database/DESIGN.md](../../DESIGN.md)). This project stays a leaf of
contracts: mechanisms (key storage, token validation, credential stores) belong
to implementations composed by the host, never here.

## Why-this-not-that decisions

- **`IDatabaseAuthenticator` lives here, not in `Database.Server`.** The server
  *drives* the handshake, but who-is-this is a security question that embedded
  hosts, replication peers, and future admin surfaces also need to answer. Homing
  the seam in the security contract project lets implementations ship without a
  dependency on the network front-end.
- **Evidence is opaque bytes, not a credential model.** The wire protocol's
  authenticate exchange carries method-specific payloads; typing them here would
  force every method (password, token, mTLS-derived proof) into one shape.
  Implementations parse the bytes they expect; the contract stays stable as
  methods are added. Structured challenge/response methods (SCRAM-style) will
  extend the seam rather than replace it — a multi-round contract is a
  deliberate later addition once a real method demands it.
- **`AllowAll` ships in the box, explicitly named for what it does.** The MVP
  server must run without a credential store, and a default deny would make the
  in-memory development loop unusable. Making the trust-everything posture an
  explicit, discoverable object (`DatabaseAuthenticator.AllowAll`) — rather than
  a silent server default — keeps the decision visible at the composition site.
  The server still defaults to it when options leave the authenticator unset;
  that default is documented as the MVP posture on the option itself.
- **Authentication and authorization are separate seams.** `IAuthorizationService`
  (principal/resource/action) is evaluated per operation; the authenticator runs
  once per session. Collapsing them invites session-scoped caching bugs.

## Error model

None of its own yet: authenticators return false rather than throw for a failed
attempt (the server maps false to the wire's `AuthenticationFailed` error);
throwing is reserved for infrastructure failures, which surface as the
implementation's own exceptions.

## AOT posture

Contracts plus one branch-free internal implementation — nothing to trim.

## Non-goals

- No principal/role/permission model here yet — it arrives with the governance
  work and the per-model security satellites.
- No credential storage or key material (see `Security.DataProtection` at the
  platform level for that machinery).
- No transport security: TLS belongs to `Connections.Security` under the server's
  listener, not to database authentication.
