# NatGateway ApplicationModel design

NatGatewayResource wraps a manifest snapshot and typed NatGatewayResourceOptions, and delegates to its internal planner. AddNatGateway returns INatGatewayResourceDescriptor, a thin graph-descriptor wrapper retaining dependencies and the built plan. The planner validates the kind, Deployment workload, declared area endpoint schemes/protocols, and http control plane at /cohesion/v1, then delegates realization to GenericPlanner. Endpoint ports, extra endpoints, secret/configuration mounts, and generic deployer options remain manifest-driven; no platform types are referenced.

SDK defaults are http/tcp:8080 and a Deployment. Replica counts use generic manifest limits; there is no Scheduler singleton constraint. The SDK keeps artifact.composable=false; the planner does not reinterpret that artifact fact.

NatGatewayResourceControlPlane.Create returns a fresh Hosting.Resources control plane with no accepted command kinds. Runtime Hosting discovers the generated registration and serves its protocol through the private Web.ControlPlane feature. Unsupported command envelopes are refused with 501; domain commands are deferred to item 31c.

The package is NuGet-only (developer-experience design D4/O2), never a framework member. Its only direct dependencies are ApplicationModel and Hosting.Resources; COHAM001 checks its full resolved closure. Public resource/options values follow PlannedResource and ResourceOptions; the descriptor is interface-first and its implementation is internal. No runtime hosting, DI, reflection serialization, or gateway implementation enters this package.
