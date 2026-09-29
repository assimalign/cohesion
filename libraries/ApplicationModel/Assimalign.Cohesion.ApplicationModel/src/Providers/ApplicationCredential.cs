using System;
using System.Text;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// A credential minted by an <see cref="IApplicationCredentialIssuer"/>.
/// </summary>
/// <param name="Scheme">The HTTP authentication scheme the credential is presented with, such as <c>Bearer</c>.</param>
/// <param name="Value">The credential value.</param>
/// <param name="ExpiresAt">The instant after which the credential is no longer valid.</param>
/// <exception cref="ArgumentException">
/// <paramref name="Scheme"/> or <paramref name="Value"/> is <see langword="null"/>, empty, or whitespace.
/// </exception>
/// <remarks>
/// <see cref="object.ToString"/> redacts <see cref="Value"/>; never log the credential itself.
/// </remarks>
public sealed record ApplicationCredential(string Scheme, string Value, DateTimeOffset ExpiresAt)
{
    /// <summary>
    /// Gets the HTTP authentication scheme the credential is presented with.
    /// </summary>
    public string Scheme { get; init; } = RequireText(Scheme, nameof(Scheme));

    /// <summary>
    /// Gets the credential value.
    /// </summary>
    public string Value { get; init; } = RequireText(Value, nameof(Value));

    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Scheme = ").Append(Scheme)
            .Append(", Value = <redacted>, ExpiresAt = ").Append(ExpiresAt.ToString("O", null));
        return true;
    }

    private static string RequireText(string value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        return value;
    }
}
