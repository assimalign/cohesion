# OpenTelemetry

The L2 transport library exports bounded OTLP/HTTP JSON logs without external SDK packages. Hosting.Telemetry supplies the resource-host Logging adapter; LogSpace.Hosting owns the Platform receiver. See [library design](Assimalign.Cohesion.OpenTelemetry/docs/DESIGN.md) for the JSON subset, ownership and deferred protocols/signals.
