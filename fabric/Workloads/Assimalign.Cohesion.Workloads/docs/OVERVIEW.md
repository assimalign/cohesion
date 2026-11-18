# Assimalign.Cohesion.Workloads

The Workloads fabric area: `Cohesion.Workloads/workloads`, something to run.

- `WorkloadSpec`, the portable core (tier T0): source (program or image), replicas, ports,
  settings (literal or `$ref`), stateful, one-shot, mounts (by `$ref` to a Storage volume),
  readiness probe, and the typed platform blocks (tier T1) by platform.
- `IWorkloadDriver<TExtension>`, the contract each platform implements, with `WorkloadView<T>` and
  `WorkloadObservation`.
- `WorkloadCapabilities`, `Support`, `SupportLevel`, `Isolation`: what a platform does with each
  portable field, and `WorkloadCapabilities.Check`, which turns that into validation findings.
- `WorkloadsJsonContext`, the source-generated JSON contract.

References only the fabric root. See [DESIGN.md](DESIGN.md).
