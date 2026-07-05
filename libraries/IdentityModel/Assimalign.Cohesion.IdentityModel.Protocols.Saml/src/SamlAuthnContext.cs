using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.IdentityModel.Protocols.Saml;

/// <summary>
/// Represents a SAML 2.0 <c>AuthnContext</c> (SAML Core §2.7.2.2): the context of the
/// authentication event. Every authentication statement carries exactly one, and it must
/// carry at least one of a class reference, a declaration reference, or an inline
/// declaration — <see cref="IsEmpty" /> reports the spec-invalid empty case for validation.
/// </summary>
public sealed class SamlAuthnContext
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SamlAuthnContext" /> class.
    /// </summary>
    /// <param name="classReference">The authentication context class reference URI.</param>
    /// <param name="declarationReference">The authentication context declaration reference URI.</param>
    /// <param name="declarationXml">The verbatim inline authentication context declaration XML, when present.</param>
    /// <param name="authenticatingAuthorities">The authenticating authorities, in order (proxied authentication). The sequence is copied.</param>
    /// <exception cref="ArgumentException">Thrown when an authenticating authority entry is null or whitespace.</exception>
    public SamlAuthnContext(
        string? classReference = null,
        string? declarationReference = null,
        string? declarationXml = null,
        IEnumerable<string>? authenticatingAuthorities = null)
    {
        ClassReference = classReference;
        DeclarationReference = declarationReference;
        DeclarationXml = declarationXml;

        if (authenticatingAuthorities is null)
        {
            AuthenticatingAuthorities = Array.Empty<string>();
        }
        else
        {
            var snapshot = new List<string>();
            foreach (var authority in authenticatingAuthorities)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(authority, nameof(authenticatingAuthorities));
                snapshot.Add(authority);
            }

            AuthenticatingAuthorities = snapshot.ToArray();
        }
    }

    /// <summary>
    /// Gets the authentication context class reference URI (see
    /// <see cref="SamlAuthnContextClasses" />).
    /// </summary>
    public string? ClassReference { get; }

    /// <summary>
    /// Gets the authentication context declaration reference URI.
    /// </summary>
    public string? DeclarationReference { get; }

    /// <summary>
    /// Gets the verbatim inline authentication context declaration XML, when present.
    /// </summary>
    public string? DeclarationXml { get; }

    /// <summary>
    /// Gets the authenticating authorities, in order (proxied authentication).
    /// </summary>
    public IReadOnlyList<string> AuthenticatingAuthorities { get; }

    /// <summary>
    /// Gets a value indicating whether the context carries none of a class reference,
    /// declaration reference, or inline declaration — a spec-invalid state a validator
    /// reports.
    /// </summary>
    public bool IsEmpty => ClassReference is null && DeclarationReference is null && DeclarationXml is null;
}
