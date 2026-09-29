namespace Assimalign.Cohesion.MessageHub;

/// <summary>
/// Defines the contract-only composition seam for a message hub application.
/// </summary>
public interface IMessageHubApplicationBuilder
{
    /// <summary>
    /// Builds the message hub application.
    /// </summary>
    /// <returns>The configured message hub application.</returns>
    IMessageHubApplication Build();
}
