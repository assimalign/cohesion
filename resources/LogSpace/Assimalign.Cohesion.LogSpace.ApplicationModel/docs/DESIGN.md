# LogSpace ApplicationModel design

LogSpaceResource wraps a manifest snapshot and typed LogSpaceResourceOptions, and delegates to its internal planner. AddLogSpace returns ILogSpaceResourceDescriptor, a thin graph-descriptor wrapper retaining dependencies and the built plan. The planner validates the kind, StatefulSet workload, declared area endpoint schemes/protocols, and query control plane at /cohesion/v1, then delegates realization to GenericPlanner. Endpoint ports, extra endpoints, secret/configuration mounts, and generic deployer options remain manifest-driven; no platform types are referenced.

The planner requires otlp https/tcp (SDK port 4318), alongside query https/tcp:8443, StatefulSet, and the data Volume at /data with 10Gi. The planner requires the sized data mount; deployer Storage.Size may override capacity. The control plane does not implement telemetry ingestion (item 31b).

LogSpaceResourceControlPlane.Create returns a fresh Hosting.Resources control plane with no accepted command kinds. Runtime Hosting discovers the generated registration and serves its protocol through the private Web.ControlPlane feature. Unsupported command envelopes are refused with 501; domain commands are deferred to item 31c.

The package is NuGet-only (developer-experience design D4/O2), never a framework member. Its only direct dependencies are ApplicationModel and Hosting.Resources; COHAM001 checks its full resolved closure. Public resource/options values follow PlannedResource and ResourceOptions; the descriptor is interface-first and its implementation is internal. No runtime hosting, DI, reflection serialization, or gateway implementation enters this package.
