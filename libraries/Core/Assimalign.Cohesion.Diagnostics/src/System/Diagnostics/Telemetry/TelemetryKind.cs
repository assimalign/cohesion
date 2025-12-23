namespace System.Diagnostics.Telemetry;

[Flags]
public enum TelemetryKind
{
    None,
    Log,
    Metric,
    Trace
}
