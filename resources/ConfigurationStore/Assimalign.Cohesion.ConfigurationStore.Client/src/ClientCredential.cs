using System;

namespace Assimalign.Cohesion.ConfigurationStore.Client;

/// <summary>
/// Carries the opaque bootstrap token presented to a configuration-store endpoint.
/// </summary>
public sealed record ClientCredential
{
    /// <summary>
    /// Initializes a client credential.
    /// </summary>
    /// <param name="token">The opaque bootstrap token.</param>
    /// <exception cref="ArgumentException"><paramref name="token"/> is empty or whitespace.</exception>
    public ClientCredential(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        Token = token;
    }

    internal string Token { get; }

    /// <summary>
    /// Returns a redacted representation that does not disclose the token.
    /// </summary>
    /// <returns>A redacted credential description.</returns>
    public override string ToString() => $"{nameof(ClientCredential)} {{ Token = [redacted] }}";
}
