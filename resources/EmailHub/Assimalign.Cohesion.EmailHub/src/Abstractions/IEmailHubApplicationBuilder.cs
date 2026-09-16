namespace Assimalign.Cohesion.EmailHub;

/// <summary>
/// Defines the contract-only composition seam for an email hub application.
/// </summary>
public interface IEmailHubApplicationBuilder
{
    /// <summary>
    /// Builds the email hub application.
    /// </summary>
    /// <returns>The configured email hub application.</returns>
    IEmailHubApplication Build();
}
