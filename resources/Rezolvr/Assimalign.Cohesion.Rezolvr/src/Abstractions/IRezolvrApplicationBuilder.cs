namespace Assimalign.Cohesion.Rezolvr;

/// <summary>
/// Defines the contract-only composition seam for a Rezolvr application.
/// </summary>
public interface IRezolvrApplicationBuilder
{
    /// <summary>
    /// Builds the Rezolvr application.
    /// </summary>
    /// <returns>The configured Rezolvr application.</returns>
    IRezolvrApplication Build();
}
