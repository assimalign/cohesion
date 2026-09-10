# Assimalign.Cohesion.ConfigurationStore.Hosting

## Summary

Provides the public `ConfigurationStoreApplication.CreateBuilder(args)` entry point and the internal,
durable ConfigurationStore host.

## Current Evaluation

- Status: code-first namespaces, durable JSON storage, authenticated HTTP protocol, default control plane
- Runtime boundary: ConfigurationStore root plus shared Hosting/Resources/Health, private Web transport, and IdentityModel JWT verification

## Primary Responsibilities

- Capture `AddNamespace` declarations as first-start seeds.
- Persist configuration as plain JSON beneath the `data` volume using atomic replacement.
- Serve list/read/set/remove operations plus standard health, endpoints, and stop routes.
- Verify gateway bootstrap JWTs against the durable trusted-issuer set.
- Preserve plain-application behavior when resource opt-in is disabled.

The protocol listener starts after user services and drains before them. Enabled resources bind the
ambient `api` endpoint; plain applications use `--endpoint` or the loopback default.

## Public type

- `ConfigurationStoreApplication` — static creation facade; all runtime implementation types are internal.
