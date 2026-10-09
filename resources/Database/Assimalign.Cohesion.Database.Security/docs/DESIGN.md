# Assimalign.Cohesion.Database.Security — Design

Security contracts shared by all five models (area architecture:
[resources/Database/DESIGN.md](../../../../docs/resources/Database/DESIGN.md)). This project stays a leaf of
contracts: mechanisms (key storage, token validation, credential stores) belong
to implementations composed by the host, never here. Its tests
(`tests/DatabaseAuthenticatorTests.cs`) pin what the base checks before an
implementation's core runs.

## Why-this-not-that decisions

- **`DatabaseAuthenticator` is an abstract class, not an interface.** The Database
  area is concrete-first (`.claude/rules/database-area.md`), and this base is an
  inverted seam: the model servers in other assemblies call it, and the application
  implements it, so its constructor is `protected`. The public `AuthenticateAsync` is
  non-virtual. It rejects a null database or principal and a canceled token, then
  calls the `protected abstract AuthenticateCoreAsync`, so an implementation never
  sees a null name or an already-canceled call. `AllowAll` is a static property on the
  base that returns an internal leaf (the `Aes.Create()` shape). The base replaced the
  `IDatabaseAuthenticator` interface and the static `DatabaseAuthenticator` factory
  class in #1258.
- **The authenticator lives here, not with the server runtime
  (`Database.Hosting`).** The server
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
- **Authentication and authorization stay separate seams.** The authenticator runs
  once per session; an authorization decision (principal/resource/action) is made
  per operation. Collapsing them invites session-scoped caching bugs. No
  authorization seam exists yet: the `IAuthorizationService` placeholder had no
  implementer and no caller, and was deleted with #1257 instead of being kept for
  later (`database-area.md`: no abstraction "for later").

## Error model

None of its own yet: authenticators return false rather than throw for a failed
attempt (the server maps false to the wire's `AuthenticationFailed` error);
throwing is reserved for infrastructure failures, which surface as the
implementation's own exceptions. The base throws only `ArgumentNullException` and
`OperationCanceledException`, before the core runs.

## Diagnostics

The project reports through one internal event source named for its assembly,
`Assimalign.Cohesion.Database.Security` (`src/Internal/EventSource/DatabaseSecurityEventSource.cs`).
The public, non-virtual `AuthenticateAsync` writes every event, so one source covers the built-in
authenticator and every application's.

| Id | Event | Level | Payload |
| --- | --- | --- | --- |
| 1 | `AuthenticationSucceeded` | Verbose | `authenticator` (the leaf's type name), `database`, `principal` |
| 2 | `AuthenticationRejected` | Verbose | `authenticator`, `database`, `principal` |
| 3 | `AuthenticationFailed` | Error | `authenticator`, `database`, `principal`, `exceptionType` (full name), `exceptionMessage` |

A verdict is Verbose: the model server's own refused-handshake warning is the operator-facing
record of a rejection. A core that throws is an infrastructure failure, so an Error. A cancellation
is not a failure and writes nothing, which narrows the plan's catalog ("when the core throws"): a
server's authentication timeout cancels the core, so it leaves no Security event, and the model
server's own handshake event is its record. The evidence is never written; the principal is (owner
question Q4 of the event-source plan). No counters.

**Nothing changes for the caller, and nothing is paid while nobody listens.** While the source is
off, `AuthenticateAsync` returns the core's task unchanged. While it is on, a verdict that completed
synchronously is reported at once and returned as a new completed task; otherwise a wrapper on a
pooling builder awaits it. A failure is written by an exception filter that declines the exception,
so it reaches the caller unchanged, and a core that throws before it returns a task still throws
from the call itself.

`DatabaseSecurityEventSourceTests` checks the name, the strict manifest, one event per verdict and
failure with its payload, the synchronous throw, the silent cancellation, and that
`AllowAll.AuthenticateAsync` allocates nothing while nobody listens.

## AOT posture

One abstract base plus one branch-free internal implementation — nothing to trim.

## Non-goals

- No principal/role/permission model here yet — it arrives with the per-model
  security satellites.
- No credential storage or key material (see `Security.DataProtection` at the
  platform level for that machinery).
- No transport security: TLS belongs to `Connections.Security` under the server's
  listener, not to database authentication.
