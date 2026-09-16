# Assimalign.Cohesion.ConfigurationStore.Hosting

## Summary

Provides the public `ConfigurationStoreApplication.CreateBuilder(args)` entry point and the public
concrete durable ConfigurationStore host, builder, and context.

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

## Public types

- `ConfigurationStoreApplication` — concrete host and creation entry point.
- `ConfigurationStoreApplicationBuilder` — composition and background-work registration.
- `ConfigurationStoreApplicationContext` — host environment and runtime state.


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

## Concrete composition (T10 / O34)

`ConfigurationStoreApplication.CreateBuilder(args)` returns the public concrete `ConfigurationStoreApplicationBuilder`; its `Build()` returns the public `ConfigurationStoreApplication : Host<ConfigurationStoreApplicationContext>`. The public `ConfigurationStoreApplicationContext` implements `IConfigurationStoreApplicationContext`, reading `ContentRootPath` from the host environment. The application explicitly forwards the root lifecycle contract to `IHost`, and consumers use the concrete application for `RunAsync` and `await using`. Runtime options and supporting services remain internal.

Background-work registration belongs to the concrete `ConfigurationStoreApplicationBuilder`: `AddService(IHostService)` and `AddService(Func<ConfigurationStoreApplicationContext, IHostService>)`. The factory deliberately receives the concrete context, unlike Web's AddService and Database's AddServer interface-context overloads, so hosting consumers can use environment, state, and hosted-service members beyond the small root contract. Factories run once per build against the same context retained by the application; the hosted-service snapshot is installed after factory evaluation. Services start in registration order and stop in reverse. No area-owned service abstraction is introduced.

The concrete application preserves the existing RunAsync shortcut for a token already cancelled
at entry: it starts and stops with uncancelled lifecycle tokens. Other runs delegate to the shared
host runner. This compatibility method hides the non-virtual base member; callers holding IHost
use the shared runner directly.
