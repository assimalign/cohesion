# Assimalign.Cohesion.IdentityHub.Models — Overview

This project contains IdentityHub's legacy tenant-directory persistence DTOs and identifier
value types. Their existing public members remain source compatible. Authenticated principals
and application credentials now point to the canonical `Assimalign.Cohesion.IdentityModel`
contracts rather than introducing IdentityHub-local claim, session, token, credential, or JWK
families.

`User.Identity` and `ServicePrincipal.Identity` carry canonical `IIdentitySubject` values.
`ApplicationCredential.Credential` carries the canonical immutable `IdentityCredential`.
