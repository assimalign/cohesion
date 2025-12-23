
using System.Threading.Tasks;

namespace System.Diagnostics.Telemetry;

/// <summary>
/// A wrapper for all telemetry.
/// </summary>
public abstract partial class TelemetryListener : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// Write a telemetry item. Uses constrained calls to avoid boxing.
    /// </summary>
    public abstract void Write<T>(in T args) where T : struct, ITelemetryArgs<T>;

    /// <summary>
    /// Write a telemetry item with strongly-typed arguments. Uses constrained calls to avoid boxing.
    /// </summary>
    public abstract void Write<T, TArguments>(in T args) where T : struct, ITelemetryArgs<T, TArguments>;

    /// <summary>
    /// Implementations may override to flush/cleanup resources.
    /// </summary>
    public virtual void Dispose() { }

    /// <summary>
    /// Implementations may override to asynchronously flush/cleanup resources.
    /// </summary>
    public virtual ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// A no-op listener that is always disabled and drops all writes.
    /// </summary>
    public static TelemetryListener Noop { get; } = new NoopTelemetryListener();

    private sealed class NoopTelemetryListener : TelemetryListener
    {
        public override void Write<T>(in T args) { }
        public override void Write<T, TArguments>(in T args) { }
    }
}