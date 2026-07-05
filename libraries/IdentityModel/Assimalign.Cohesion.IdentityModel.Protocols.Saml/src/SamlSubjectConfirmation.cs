using System;

namespace Assimalign.Cohesion.IdentityModel.Protocols.Saml;

/// <summary>
/// Represents a SAML 2.0 <c>SubjectConfirmation</c> (SAML Core §2.4.1): a mechanism by which
/// a subject can be confirmed, its method, and the constraining data.
/// </summary>
public sealed class SamlSubjectConfirmation
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SamlSubjectConfirmation" /> class.
    /// </summary>
    /// <param name="method">The confirmation method URI (see <see cref="SamlConfirmationMethods" />).</param>
    /// <param name="nameId">The confirming NameID, when the confirmation identifies its own subject.</param>
    /// <param name="data">The constraining confirmation data.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="method" /> is null or whitespace.</exception>
    public SamlSubjectConfirmation(
        string method,
        SamlNameId? nameId = null,
        SamlSubjectConfirmationData? data = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);

        Method = method;
        NameId = nameId;
        Data = data;
    }

    /// <summary>
    /// Gets the confirmation method URI.
    /// </summary>
    public string Method { get; }

    /// <summary>
    /// Gets the confirming NameID, when present.
    /// </summary>
    public SamlNameId? NameId { get; }

    /// <summary>
    /// Gets the constraining confirmation data, when present.
    /// </summary>
    public SamlSubjectConfirmationData? Data { get; }

    /// <summary>
    /// Gets a value indicating whether this is a bearer confirmation.
    /// </summary>
    public bool IsBearer => string.Equals(Method, SamlConfirmationMethods.Bearer, StringComparison.Ordinal);
}
