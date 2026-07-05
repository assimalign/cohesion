namespace Assimalign.Cohesion.IdentityModel.Protocols.OpenIdConnect;

/// <summary>
/// Defines the <c>prompt</c> parameter values (OpenID Connect Core 1.0 §3.1.2.1).
/// </summary>
public static class OpenIdConnectPromptValues
{
    /// <summary>
    /// No user interface may be shown (<c>none</c>). Must not be combined with the other
    /// prompt values.
    /// </summary>
    public const string None = "none";

    /// <summary>
    /// The user should be prompted to re-authenticate (<c>login</c>).
    /// </summary>
    public const string Login = "login";

    /// <summary>
    /// The user should be prompted for consent (<c>consent</c>).
    /// </summary>
    public const string Consent = "consent";

    /// <summary>
    /// The user should be prompted to select an account (<c>select_account</c>).
    /// </summary>
    public const string SelectAccount = "select_account";
}
