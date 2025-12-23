namespace System.Diagnostics.Telemetry;

public abstract class TelemetrySource
{
    protected TelemetrySource(string name)
    {
        Name = name;
    }

    /// <summary>
    /// 
    /// </summary>
    public string Name { get; }
}
