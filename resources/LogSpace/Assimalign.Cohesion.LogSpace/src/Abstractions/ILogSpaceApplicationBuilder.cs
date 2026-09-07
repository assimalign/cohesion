using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.LogSpace;

/// <summary>
/// Defines the contract-only composition seam for a LogSpace application.
/// </summary>
public interface ILogSpaceApplicationBuilder : IHostBuilder
{
    /// <summary>
    /// Builds the LogSpace application.
    /// </summary>
    /// <returns>The configured LogSpace application.</returns>
    new ILogSpaceApplication Build();
}
