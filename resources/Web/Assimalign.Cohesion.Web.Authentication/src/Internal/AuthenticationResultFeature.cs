namespace Assimalign.Cohesion.Web.Authentication;

/// <summary>
/// Default <see cref="IAuthenticationResultFeature"/> implementation — a typed holder for the
/// default-scheme <see cref="AuthenticateResult"/>.
/// </summary>
internal sealed class AuthenticationResultFeature : IAuthenticationResultFeature
{
    /// <inheritdoc />
    public string Name => nameof(IAuthenticationResultFeature);

    /// <inheritdoc />
    public AuthenticateResult? AuthenticateResult { get; set; }
}
