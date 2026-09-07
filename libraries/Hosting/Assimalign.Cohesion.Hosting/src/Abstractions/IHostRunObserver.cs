namespace Assimalign.Cohesion.Hosting;

/// <summary>
/// Observes the lifecycle transitions of one host run.
/// </summary>
public interface IHostRunObserver
{
    /// <summary>
    /// Reports that the host completed startup.
    /// </summary>
    /// <param name="host">The running host.</param>
    void Started(IHost host);

    /// <summary>
    /// Reports that the host accepted a stop transition.
    /// </summary>
    /// <param name="host">The stopping host.</param>
    void Stopping(IHost host);

    /// <summary>
    /// Reports that the graceful drain budget was cancelled.
    /// </summary>
    /// <param name="host">The draining host.</param>
    void DrainAborted(IHost host);

    /// <summary>
    /// Reports that the host completed its stop sequence.
    /// </summary>
    /// <param name="host">The stopped host.</param>
    void Stopped(IHost host);
}
