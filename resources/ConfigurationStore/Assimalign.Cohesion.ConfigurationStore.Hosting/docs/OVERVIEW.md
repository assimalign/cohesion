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


## Declarative commands

Configuration commands now register runtime handlers on the same IResourceControlPlane used by direct
in-process delivery. The HTTP adapter authenticates first, preserving issuer/owner equality (403),
missing namespaces (404), and unsupported kinds (501), with status/detail JSON on command refusals.
Other ownership refusals return 409. POST continues to accept the existing envelope and set payload
{value}; typed declarations can additionally include namespace/key, which must match the envelope key.
Configuration keys cannot contain `/`; namespaces may contain it, preserving one ownership identity.
DELETE commands uses the same envelope: removing a set declaration removes its value; removing a
remove-value declaration releases ownership without restoring an undeclared historical value.

## Commands

| Wire kind | Descriptor verb | Ownership key |
|---|---|---|
| `configurationstore.add-namespace` | `AddNamespace` | namespace name |

AddNamespace creates a namespace if absent and atomically stores its owner and original seed
alongside values. An identical declaration succeeds even after separate value commands change its
contents. A different seed or foreign owner is rejected with a named detail. Resource-seeded namespaces
are not implicitly adopted. Deletion removes the owned namespace; callers should remove its value
commands first. Existing SetValue and RemoveValue behavior remains unchanged, including 404 for
unknown namespaces.
