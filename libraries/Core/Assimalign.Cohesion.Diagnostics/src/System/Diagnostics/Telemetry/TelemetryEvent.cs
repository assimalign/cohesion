namespace System.Diagnostics.Telemetry;

public readonly struct TelemetryEvent
{
    public TelemetryEvent(string name, TelemetrySeverity severity)
    {
        ArgumentNullException.ThrowIfNull(name);
        Name = name;
        Severity = severity;
    }
    public string Name { get; }
    public TelemetrySeverity Severity { get; }
    public override string ToString() => Name;
}
