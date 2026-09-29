# IdentityHubClientOptions

Namespace: `Assimalign.Cohesion.IdentityHub`
Assembly: `Assimalign.Cohesion.IdentityHub`

`IdentityHubClientOptions` configures one code-first OAuth client registration.

- `ClientSecret` enables confidential `client_credentials` authentication when non-null. Secrets must not be empty or whitespace and are retained by Hosting only as a SHA-256 digest.
- `AllowDeviceAuthorization` enables the OAuth device authorization grant.
- `AccessTokenLifetime` defaults to one hour and must be greater than zero and no more than 24 hours.
- `Audiences` lists exact audiences the client may request. Every entry must also be declared with `AddAudience`.

A client must enable at least one grant and allow at least one audience. Options are snapshotted during `AddClient`; later mutations of the caller-owned options do not change the registration.
