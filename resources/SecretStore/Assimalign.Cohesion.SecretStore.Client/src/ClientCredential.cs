using System;

namespace Assimalign.Cohesion.SecretStore.Client;

/// <summary>
/// Carries the opaque bearer token presented to a secret-store endpoint.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("ClientCredential")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
public sealed record ClientCredential
{
    /// <summary>
    /// Initializes a client credential.
    /// </summary>
    /// <param name="token">The opaque bearer token.</param>
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

    private sealed class DebugView
    {
        public DebugView(ClientCredential value)
        {
        }
    }
}
