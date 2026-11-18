# Assimalign.Cohesion.Networking

The Networking fabric area: `Cohesion.Networking/exposures`, who may reach a workload's port.
`ExposureSpec` targets a workload through a `$ref` and names a port and an `ExposureVisibility`
(internal or public). `NetworkingJsonContext` is its source-generated JSON contract.

References only the fabric root. See [DESIGN.md](DESIGN.md).
