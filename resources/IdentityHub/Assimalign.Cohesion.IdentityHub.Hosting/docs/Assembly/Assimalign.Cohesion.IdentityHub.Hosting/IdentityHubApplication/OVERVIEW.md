# IdentityHubApplication

Namespace: `Assimalign.Cohesion.IdentityHub.Hosting`

Assembly: `Assimalign.Cohesion.IdentityHub.Hosting`

`IdentityHubApplication.CreateBuilder(args)` creates the code-first IdentityHub builder for the executable resource assembly. Build validates registrations, captures ambient resource inputs, resolves the `https` endpoint and `data` mount, materializes additional services in registration order, and appends the built-in issuer service.

The issuer is registered with ResourceRuntime when a generated default control plane exists. `--endpoint` and `--data` provide standalone overrides when no corresponding ambient input is present. A materialized `tls` mount supplies production PEM certificate material; the built-in self-signed fallback and device-approval page are restricted to loopback Local.
