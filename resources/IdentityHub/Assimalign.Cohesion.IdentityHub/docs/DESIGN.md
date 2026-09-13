# Assimalign.Cohesion.IdentityHub Design

## Application seam

The area root owns the public contracts composed by an IdentityHub resource. `IIdentityHubApplicationBuilder` declares token audiences with `AddAudience`, registers OAuth clients with `AddClient`, accepts optional lifecycle services, and builds `IIdentityHubApplication`. Concrete hosting remains internal to `Assimalign.Cohesion.IdentityHub.Hosting`.

Client registration is intentionally code-first. A non-empty `ClientSecret` enables `client_credentials`; `AllowDeviceAuthorization` enables the device grant. Clients explicitly list the audiences they may request, and build fails if a client names an undeclared audience or enables no grant. Corresponding declarative gateway commands now ship in IdentityHub.ApplicationModel; Hosting combines their durable registry with these builder registrations.

## Security boundary

The public options carry a client secret only during composition. Hosting snapshots options and retains only a SHA-256 digest, compared in fixed time. Access-token lifetimes are positive and capped at 24 hours. IdentityHub builds protocol tokens on the shared IdentityModel and JWT contracts instead of defining competing token, claim, subject, credential, or session primitives.

## Hosting isolation and AOT

The root references only the shared Hosting and IdentityModel contracts. HTTP serving, persistence, cryptography, and ResourceRuntime integration belong to the Hosting module. The surface requires no runtime scanning, dynamic code generation, or container activation and remains trimming- and NativeAOT-safe.
