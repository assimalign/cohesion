# OpenTelemetry

The L2 transport library exports bounded OTLP/HTTP JSON logs without external SDK packages. No Logging adapter ships with it; LogSpace.Hosting owns the OTLP receiver. See [library design](Assimalign.Cohesion.OpenTelemetry/docs/DESIGN.md) for the JSON subset, ownership and deferred protocols/signals.
