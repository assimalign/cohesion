# Assimalign.Cohesion.Fabric.InProcess

The in-process platform, `inprocess`: runs an application's programs inside one process for
local development, through the same contract as the out-of-process platforms.

- `InProcessPlatform`, a `FabricPlatform` that registers its fault classifier and its typed
  workload extension, and publishes `WorkloadCapabilities`.
- `InProcessWorkloadSettings`, the typed workload block at `spec.platforms.inprocess`.
- `InProcessFaultReasons`, the reasons the platform reports.

References the three fabric areas. It does not run programs yet; see [DESIGN.md](DESIGN.md).
