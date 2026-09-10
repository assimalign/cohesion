# Assimalign.Cohesion.IdentityHub.ApplicationModel — Design

## Intent

IdentityHub owns its platform-neutral orchestration decisions without owning platform
objects. An enabled `Sdk.IdentityHub` executable produces the generic resource manifest;
this package validates its area shape and turns it into `cohesion/plan/v1` at application
build time.

## Planner contract

`IdentityHubResource` is a `PlannedResource` whose diagnostic name is `IdentityHub
planner`. The planner requires kind `IdentityHub`, a `StatefulSet`, an HTTPS-over-TCP
endpoint named `https`, and exactly one persistent `data` Volume. Generic planning must
then produce one stable replica, a sized per-replica claim, one HTTPS service, and one
portless headless governing service. Additional endpoints and non-persistent Configuration
or Secret mounts remain legal generic traits. Scaling is rejected until IdentityHub
defines a replication protocol.

`IdentityHubResourceOptions.Storage.Size` may replace the manifest claim size. Endpoint
ports, paths, exposure, mounts, and lifecycle defaults remain manifest facts.

## Default control plane

`IdentityHubResourceControlPlane.Create()` returns a fresh
`Hosting.Resources.IResourceControlPlane`. Generated `ResourceControlPlane.g.cs` registers
the factory for enabled executables, and IdentityHub.Hosting serves it through the shared
runtime seam without either package referencing the other. Accepted command kinds are
intentionally empty; the gateway/control-plane command counterparts of the runtime
`AddAudience` and `AddClient` verbs are deferred to item 31c.

## AOT and dependency posture

COHAM001 restricts the closure to Core, ApplicationModel, plain Hosting, Hosting.Health,
Hosting.Resources, ProtectedData, and BCL assemblies. Planning uses typed records and
ordinary loops; golden serialization uses `ResourcePlanJsonContext`. There is no runtime
reflection, dynamic generation, gateway dependency, or platform-specific model.

## Non-goals

- Hosting IdentityHub endpoints or implementing identity protocols.
- Defining token, claim, session, credential, or JWK models.
- Adding gateway resource-command descriptors before item 31c.
- Carrying Kubernetes, Docker, or other platform objects.
