# Assimalign.Cohesion.Database.Security — Overview

The model-agnostic security contracts of the Data Platform: who a connection is
(`DatabaseAuthenticator`). Model-specific security features (SQL grants,
document-collection ACLs, …) live in the per-model `*.Security` satellites and
build on this seam.

## Scope

- **Authentication** — `DatabaseAuthenticator` verifies the principal a client
  claims during the wire-protocol handshake, given whatever evidence bytes the
  client's authentication response carries. It is an abstract class: an
  application derives from it and implements `AuthenticateCoreAsync`.
  `DatabaseAuthenticator.AllowAll` is the built-in trust-everything implementation
  (MVP/development posture).
- **Authorization** — not modeled yet. The `IAuthorizationService` placeholder had no
  implementer and no caller and was deleted under the concrete-first program (#1257);
  an authorization surface arrives with a real consumer.

## Dependencies

None — a leaf contract project.

## Consumers

The per-model wire servers drive `DatabaseAuthenticator` from their session
handshake; each server's options take one (`Authenticator`, `AllowAll` when unset).

See [DESIGN.md](DESIGN.md) for the seam decisions and the MVP posture.
