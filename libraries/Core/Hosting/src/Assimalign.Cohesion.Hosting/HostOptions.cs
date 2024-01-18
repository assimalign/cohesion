using System;

namespace Assimalign.Cohesion.Hosting;

/// <summary>
/// The default host options 
/// </summary>
public sealed class HostOptions
{
    /// <summary>
    /// Specify the timespan interval in which to check each server's state. The default is 5 seconds.
    /// </summary>
    public TimeSpan StateCheckInterval { get; set; } = TimeSpan.FromSeconds(5);
    /// <summary>
    /// Gracefully shutdown all servers if one fails to start successfully. The default is 'true'.
    /// </summary>
    public bool StopAllServersOnSingleFailure { get; set; } = true;

}
