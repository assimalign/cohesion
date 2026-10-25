# Hosting management overview

Resources advertise `web.cors` and `web.tls.certificate` state through Hosting.Resources. The CORS
adapter publishes version and policies together. The certificate adapter resolves an explicit
immutable provider version and selects it for new TLS handshakes. Private certificate key material never appears
in the manifest or state observations. Existing TLS sessions retain their certificate/session.

Application Model preserves this metadata opaquely. Gateways authorize, persist and distribute
desired revisions. This package references neither Application Model nor a platform gateway.

The package is opt-in and NuGet-only. It does not add management dependencies to plain Web.Hosting,
Web.Cors or the other resource frameworks. See [design](DESIGN.md) for lifecycle and trust boundaries.
