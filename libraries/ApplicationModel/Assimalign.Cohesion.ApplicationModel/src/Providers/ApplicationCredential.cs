using System;
using System.Diagnostics;
using System.Text;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// A credential minted by an <see cref="IApplicationCredentialIssuer"/>.
/// </summary>
/// <param name="Scheme">The HTTP authentication scheme the credential is presented with, such as <c>Bearer</c>.</param>
/// <param name="Value">The credential value.</param>
/// <param name="ExpiresAt">The instant after which the credential is no longer valid.</param>
/// <exception cref="ArgumentException">
/// <paramref name="Scheme"/> or <paramref name="Value"/> is <see langword="default"/>.
/// </exception>
/// <remarks>
/// <see cref="object.ToString"/> redacts <see cref="Value"/>; never log the credential itself.
/// </remarks>
[DebuggerDisplay("{Scheme,nq} <redacted>")]
public sealed record ApplicationCredential(
    ApplicationCredentialScheme Scheme,
    ApplicationCredentialValue Value,
    DateTimeOffset ExpiresAt)
{
    private ApplicationCredentialScheme _scheme = RequireScheme(Scheme);
    private ApplicationCredentialValue _value = RequireValue(Value);

    /// <summary>
    /// Gets the HTTP authentication scheme the credential is presented with.
    /// </summary>
    /// <exception cref="ArgumentException">The assigned scheme is <see langword="default"/>.</exception>
    public ApplicationCredentialScheme Scheme
    {
        get => _scheme;
        init => _scheme = RequireScheme(value);
    }

    /// <summary>
    /// Gets the credential value.
    /// </summary>
    /// <exception cref="ArgumentException">The assigned value is <see langword="default"/>.</exception>
    public ApplicationCredentialValue Value
    {
        get => _value;
        init => _value = RequireValue(value);
    }

    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Scheme = ").Append(Scheme)
            .Append(", Value = <redacted>, ExpiresAt = ").Append(ExpiresAt.ToString("O", null));
        return true;
    }

    private static ApplicationCredentialScheme RequireScheme(ApplicationCredentialScheme value)
    {
        if (value.Value is null)
        {
            throw new ArgumentException(ErrorMessages.ApplicationCredentialSchemeInvalid, nameof(Scheme));
        }

        return value;
    }

    private static ApplicationCredentialValue RequireValue(ApplicationCredentialValue value)
    {
        if (value.Value is null)
        {
            throw new ArgumentException(ErrorMessages.ApplicationCredentialValueInvalid, nameof(Value));
        }

        return value;
    }
}
