using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.Scheduler;

/// <summary>
/// Defines the contract-only composition seam for a scheduler application.
/// </summary>
public interface ISchedulerApplicationBuilder : IHostBuilder
{
    /// <summary>
    /// Builds the scheduler application.
    /// </summary>
    /// <returns>The configured scheduler application.</returns>
    new ISchedulerApplication Build();
}
