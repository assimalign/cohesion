# Assimalign.Cohesion.Storage

The Storage fabric area: `Cohesion.Storage/volumes`, storage that outlives the workloads that
mount it. `VolumeSpec` holds a capacity and a `VolumeAccessMode`; a workload mounts a volume
through a `$ref`. `StorageJsonContext` is its source-generated JSON contract.

References only the fabric root. See [DESIGN.md](DESIGN.md).
