# Assimalign.Cohesion.IdentityHub.Hosting

## Summary

Provides `IdentityHubApplication.CreateBuilder(args)` and the internal, AOT-safe OpenID Connect issuer runtime.

The host serves discovery, persisted ES256 JWKS, client-credentials tokens, public health probes, and the bootstrap-authenticated Cohesion resource control plane. Configuration is code-first through `AddAudience` and `AddClient`; the generated resource manifest supplies the `https` endpoint and `data` volume. Only `openid` and an empty scope are accepted.

Device authorization and its fixed-subject browser approval page are exposed only by a loopback Development endpoint. Applications needing account login, subject selection, consent, recovery, or federation compose an authenticated user-facing flow separately.

HTTPS reads a PEM certificate, private key, and optional chain from a materialized `tls` mount. Without that mount, self-signed TLS is limited to loopback Development; non-development HTTPS fails closed rather than generating production TLS material.
