# SecretStoreResourceControlPlane

Namespace: `Assimalign.Cohesion.SecretStore.ApplicationModel`

Assembly: `Assimalign.Cohesion.SecretStore.ApplicationModel`

## Purpose

`SecretStoreResourceControlPlane` supplies the default-control-plane factory used by
enabled secret-store executables.

## Create

```csharp
IResourceControlPlane controlPlane = SecretStoreResourceControlPlane.Create();
```

Every call returns a fresh, isolated plane with no observed endpoints and one accepted
generic trust command, `cohesion.trust.add`. The plane aggregates health, readiness,
and liveness, records observed endpoints, and carries graceful-stop and command
operations.

Generated `ResourceControlPlane.g.cs` registers the factory with `ResourceRuntime` and
observes the invocation's endpoints before `SecretStore.Hosting` serves the standard
control-plane and store-protocol routes under `/cohesion/v1` on `api`.

The factory does not host HTTP, persist secrets, verify credentials, or issue
certificates. Those are runtime responsibilities. SecretStore-specific desired-state
commands and their typed descriptor verbs remain developer-experience item 31c work.

## Links

- [Assembly overview](../OVERVIEW.md)
- [Project design](../../../DESIGN.md)
