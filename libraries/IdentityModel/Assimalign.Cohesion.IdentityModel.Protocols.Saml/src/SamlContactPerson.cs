namespace Assimalign.Cohesion.IdentityModel.Protocols.Saml;

/// <summary>
/// Represents a SAML 2.0 metadata <c>ContactPerson</c> (SAML Metadata §2.3.2.2).
/// </summary>
public sealed class SamlContactPerson
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SamlContactPerson" /> class.
    /// </summary>
    /// <param name="contactType">The contact type (<c>technical</c>, <c>support</c>, <c>administrative</c>, <c>billing</c>, or <c>other</c>).</param>
    /// <param name="company">The contact company.</param>
    /// <param name="givenName">The contact given name.</param>
    /// <param name="surName">The contact surname.</param>
    /// <param name="emailAddress">The contact email address.</param>
    public SamlContactPerson(
        string? contactType = null,
        string? company = null,
        string? givenName = null,
        string? surName = null,
        string? emailAddress = null)
    {
        ContactType = contactType;
        Company = company;
        GivenName = givenName;
        SurName = surName;
        EmailAddress = emailAddress;
    }

    /// <summary>
    /// Gets the contact type.
    /// </summary>
    public string? ContactType { get; }

    /// <summary>
    /// Gets the contact company.
    /// </summary>
    public string? Company { get; }

    /// <summary>
    /// Gets the contact given name.
    /// </summary>
    public string? GivenName { get; }

    /// <summary>
    /// Gets the contact surname.
    /// </summary>
    public string? SurName { get; }

    /// <summary>
    /// Gets the contact email address.
    /// </summary>
    public string? EmailAddress { get; }
}
