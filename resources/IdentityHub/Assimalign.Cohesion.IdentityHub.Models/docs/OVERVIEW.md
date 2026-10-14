# Assimalign.Cohesion.IdentityHub.Models — Overview

This project contains IdentityHub's legacy tenant-directory persistence DTOs and identifier
value types. The DTOs retain their public shapes; generated value-type compatibility changes
and migration guidance are recorded in [DESIGN.md](DESIGN.md#generated-string-value-type-compatibility-1472).

Authenticated principals and application credentials now point to the canonical
`Assimalign.Cohesion.IdentityModel`
contracts rather than introducing IdentityHub-local claim, session, token, credential, or JWK
families.

`User.Identity` and `ServicePrincipal.Identity` carry canonical `IIdentitySubject` values.
`ApplicationCredential.Credential` carries the canonical immutable `IdentityCredential`.
