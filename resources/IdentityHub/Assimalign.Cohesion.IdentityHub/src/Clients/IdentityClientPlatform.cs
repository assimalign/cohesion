namespace Assimalign.Cohesion.IdentityHub;

/// <summary>
/// The kind of application a redirect URI returns to, which decides the URIs it may be. The
/// member values are stable and additive-only.
/// </summary>
public enum IdentityClientPlatform
{
    /// <summary>
    /// No platform. This is the platform of a <see langword="default"/> redirect, which every
    /// registration boundary refuses.
    /// </summary>
    None = 0,

    /// <summary>
    /// A web application that redeems the authorization code on its server. Its redirect URIs
    /// are <c>https</c>, or <c>http</c> on a loopback host during development.
    /// </summary>
    Web = 1,

    /// <summary>
    /// A single-page application that redeems the authorization code in the browser. Its
    /// redirect URIs follow the same rule as <see cref="Web"/>.
    /// </summary>
    SinglePage = 2,

    /// <summary>
    /// A mobile or desktop application, as RFC 8252 describes it. Its redirect URIs are
    /// <c>https</c>, <c>http</c> on a loopback host, or a private-use scheme that is a reversed
    /// domain name, such as <c>com.example.app:/callback</c>.
    /// </summary>
    Native = 3,
}
