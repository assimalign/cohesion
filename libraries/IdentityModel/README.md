# IdentityModel

The shared authentication and identity contract foundation for Cohesion. Every
resource under `resources/*` that authenticates a caller — and IdentityHub
itself — depends on this family for one normalized identity surface instead of
inventing service-local identity types.

## Projects

| Project | Role |
|---|---|
| `Assimalign.Cohesion.IdentityModel` | The dependency anchor. Canonical identity domain model (subjects, application identities, credentials, claims and attributes, sessions, authentication results) plus the protocol abstractions and first-class contract branches for OpenID Connect and SAML 2.0. |
| `Assimalign.Cohesion.IdentityModel.Token` | Protocol-neutral token and assertion normalization between the root contracts and the concrete token packages. |
| `Assimalign.Cohesion.IdentityModel.Token.JsonWebToken` | Concrete JOSE / JWT document behavior (compact serialization, header and claim fidelity, validation descriptors). |
| `Assimalign.Cohesion.IdentityModel.Token.Saml` | Concrete SAML 2.0 assertion token behavior (statements, conditions, subject confirmation fidelity). |

## Layering

IdentityModel is an L1 foundation library family (see `docs/DELIVERY_ROADMAP.md`
for the layering model). It sits below every service platform: L2 runtime
composition and L3 service platforms (IdentityHub, Web, Database, …) consume
these contracts; nothing in this family depends on hosting, transport, or
service runtime concerns.

Dependency direction is strictly one-way, toward the root:

```
Assimalign.Cohesion.IdentityModel            (no Cohesion dependencies)
    ▲
Assimalign.Cohesion.IdentityModel.Token
    ▲                        ▲
…Token.JsonWebToken     …Token.Saml          (siblings never reference each other)
```

Protocol *contracts* — including the OpenID Connect and SAML 2.0 branches —
live in the root library. Descendant packages own concrete token *document*
behavior only. Future implementation packages (protocol readers, writers,
metadata handlers, validators) may be added as descendants, but the contracts
they implement stay in the root.

## Dependencies

- None. The root package references only the BCL, and the family references
  only itself in the direction shown above. No `Microsoft.Extensions.*`, no
  transport, no serializer dependencies.

## Further Reading

- [Assimalign.Cohesion.IdentityModel/docs/OVERVIEW.md](Assimalign.Cohesion.IdentityModel/docs/OVERVIEW.md)
- [Assimalign.Cohesion.IdentityModel/docs/DESIGN.md](Assimalign.Cohesion.IdentityModel/docs/DESIGN.md) —
  the family design: ownership boundaries, namespace map, dependency rules,
  standards references, and non-goals.
