

namespace Assimalign.Cohesion.Resilience.Telemetry;

/// <summary>
/// Listener of resilience telemetry events.
/// </summary>
public abstract class ResilienceTelemetryListener
{
    /// <summary>
    /// Writes a resilience event to the listener.
    /// </summary>
    /// <typeparam name="TArgs">The type of arguments associated with the event.</typeparam>
    /// <param name="args">The arguments associated with the event.</param>
    public abstract void Write<TArgs>(in ResilienceEventArgs<TArgs> args);

    /// <summary>
    /// Writes a resilience event to the listener.
    /// </summary>
    /// <typeparam name="TResult">The result type.</typeparam>
    /// <typeparam name="TArgs">The type of arguments associated with the event.</typeparam>
    /// <param name="args">The arguments associated with the event.</param>
    public abstract void Write<TResult, TArgs>(in ResilienceEventArgs<TResult, TArgs> args);
}
