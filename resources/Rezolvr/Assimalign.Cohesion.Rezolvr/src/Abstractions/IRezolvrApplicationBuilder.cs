using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.Rezolvr;

/// <summary>
/// Defines the contract-only composition seam for a Rezolvr application.
/// </summary>
public interface IRezolvrApplicationBuilder : IHostBuilder
{
    /// <summary>
    /// Builds the Rezolvr application.
    /// </summary>
    /// <returns>The configured Rezolvr application.</returns>
    new IRezolvrApplication Build();
}
