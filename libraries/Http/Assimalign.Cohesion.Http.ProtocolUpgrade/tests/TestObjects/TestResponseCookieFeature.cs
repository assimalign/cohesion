namespace Assimalign.Cohesion.Http.ProtocolUpgrade.Tests.TestObjects;

/// <summary>
/// An <see cref="IHttpResponseCookieFeature"/> over a caller-supplied collection: bound to the response
/// headers it stands for the default feature, and detached from them it stands for a replacement feature
/// that has fallen out of sync with <c>Set-Cookie</c>.
/// </summary>
internal sealed class TestResponseCookieFeature : IHttpResponseCookieFeature
{
    public TestResponseCookieFeature(IHttpCookieCollection cookies)
    {
        Cookies = cookies;
    }

    /// <inheritdoc />
    public string Name => nameof(TestResponseCookieFeature);

    /// <inheritdoc />
    public IHttpCookieCollection Cookies { get; }
}
