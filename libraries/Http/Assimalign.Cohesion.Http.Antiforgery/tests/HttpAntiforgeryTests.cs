using System.Threading.Tasks;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Antiforgery.Tests;

public class HttpAntiforgeryTests
{
    [Fact(DisplayName = "Cohesion Test [Http.Antiforgery] - Validate: Should accept a freshly minted cookie + header token pair")]
    public async Task ValidateRequest_OnValidPairViaHeader_ShouldPass()
    {
        // Arrange
        HttpAntiforgeryOptions options = new();
        IHttpAntiforgery antiforgery = HttpAntiforgery.Create(options);
        TestHttpContext mint = new(HttpMethod.Get);
        HttpAntiforgeryTokenSet tokens = antiforgery.GetAndStoreTokens(mint);

        TestHttpContext post = new(HttpMethod.Post);
        post.SetRequestCookie(options.CookieName, tokens.CookieToken!);
        post.SetRequestHeader(options.HeaderName, tokens.RequestToken!);

        // Act
        bool valid = await antiforgery.IsRequestValidAsync(post);

        // Assert
        valid.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Antiforgery] - Validate: Should accept a valid token pair submitted via form field")]
    public async Task ValidateRequest_OnValidPairViaForm_ShouldPass()
    {
        HttpAntiforgeryOptions options = new();
        IHttpAntiforgery antiforgery = HttpAntiforgery.Create(options);
        TestHttpContext mint = new(HttpMethod.Get);
        HttpAntiforgeryTokenSet tokens = antiforgery.GetAndStoreTokens(mint);

        TestHttpContext post = new(HttpMethod.Post);
        post.SetRequestCookie(options.CookieName, tokens.CookieToken!);
        post.SetFormField(options.FormFieldName, tokens.RequestToken!);

        bool valid = await antiforgery.IsRequestValidAsync(post);

        valid.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Antiforgery] - Validate: Should skip validation for safe methods")]
    public async Task ValidateRequest_OnSafeMethod_ShouldPassWithoutTokens()
    {
        IHttpAntiforgery antiforgery = HttpAntiforgery.Create();

        foreach (HttpMethod method in new[] { HttpMethod.Get, HttpMethod.Head, HttpMethod.Options, HttpMethod.Trace })
        {
            TestHttpContext context = new(method);
            (await antiforgery.IsRequestValidAsync(context)).ShouldBeTrue();
        }
    }

    [Fact(DisplayName = "Cohesion Test [Http.Antiforgery] - Validate: Should reject a tampered request token")]
    public async Task ValidateRequest_OnTamperedRequestToken_ShouldFail()
    {
        HttpAntiforgeryOptions options = new();
        IHttpAntiforgery antiforgery = HttpAntiforgery.Create(options);
        TestHttpContext mint = new(HttpMethod.Get);
        HttpAntiforgeryTokenSet tokens = antiforgery.GetAndStoreTokens(mint);

        TestHttpContext post = new(HttpMethod.Post);
        post.SetRequestCookie(options.CookieName, tokens.CookieToken!);
        post.SetRequestHeader(options.HeaderName, Tamper(tokens.RequestToken!));

        (await antiforgery.IsRequestValidAsync(post)).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Antiforgery] - Validate: Should reject when the cookie token is missing")]
    public async Task ValidateRequest_OnMissingCookie_ShouldFail()
    {
        HttpAntiforgeryOptions options = new();
        IHttpAntiforgery antiforgery = HttpAntiforgery.Create(options);
        HttpAntiforgeryTokenSet tokens = antiforgery.GetAndStoreTokens(new TestHttpContext(HttpMethod.Get));

        TestHttpContext post = new(HttpMethod.Post);
        post.SetRequestHeader(options.HeaderName, tokens.RequestToken!);
        // No cookie installed.

        (await antiforgery.IsRequestValidAsync(post)).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Antiforgery] - Validate: Should reject when the request token is missing")]
    public async Task ValidateRequest_OnMissingRequestToken_ShouldFail()
    {
        HttpAntiforgeryOptions options = new();
        IHttpAntiforgery antiforgery = HttpAntiforgery.Create(options);
        HttpAntiforgeryTokenSet tokens = antiforgery.GetAndStoreTokens(new TestHttpContext(HttpMethod.Get));

        TestHttpContext post = new(HttpMethod.Post);
        post.SetRequestCookie(options.CookieName, tokens.CookieToken!);
        // No request token in header or form.

        (await antiforgery.IsRequestValidAsync(post)).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Antiforgery] - Validate: Should reject a request token bound to a different cookie")]
    public async Task ValidateRequest_OnMismatchedPair_ShouldFail()
    {
        HttpAntiforgeryOptions options = new();
        IHttpAntiforgery antiforgery = HttpAntiforgery.Create(options);

        HttpAntiforgeryTokenSet pairA = antiforgery.GetTokens(new TestHttpContext(HttpMethod.Get));
        HttpAntiforgeryTokenSet pairB = antiforgery.GetTokens(new TestHttpContext(HttpMethod.Get));

        TestHttpContext post = new(HttpMethod.Post);
        post.SetRequestCookie(options.CookieName, pairA.CookieToken!);   // cookie A
        post.SetRequestHeader(options.HeaderName, pairB.RequestToken!);  // request token bound to B

        (await antiforgery.IsRequestValidAsync(post)).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Antiforgery] - Validate: Should reject garbage tokens without throwing")]
    public async Task ValidateRequest_OnGarbageTokens_ShouldFail()
    {
        HttpAntiforgeryOptions options = new();
        IHttpAntiforgery antiforgery = HttpAntiforgery.Create(options);

        TestHttpContext post = new(HttpMethod.Post);
        post.SetRequestCookie(options.CookieName, "not-a-valid-token!!");
        post.SetRequestHeader(options.HeaderName, "also-garbage");

        (await antiforgery.IsRequestValidAsync(post)).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Antiforgery] - Validate: Should reject a pair minted under a different key")]
    public async Task ValidateRequest_OnWrongKey_ShouldFail()
    {
        HttpAntiforgeryOptions optionsA = new();
        HttpAntiforgeryOptions optionsB = new();
        IHttpAntiforgery serviceA = HttpAntiforgery.Create(optionsA);
        IHttpAntiforgery serviceB = HttpAntiforgery.Create(optionsB);

        HttpAntiforgeryTokenSet tokens = serviceA.GetAndStoreTokens(new TestHttpContext(HttpMethod.Get));

        TestHttpContext post = new(HttpMethod.Post);
        post.SetRequestCookie(optionsA.CookieName, tokens.CookieToken!);
        post.SetRequestHeader(optionsA.HeaderName, tokens.RequestToken!);

        // serviceB has a different random key, so the cookie token's signature
        // does not validate.
        (await serviceB.IsRequestValidAsync(post)).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Antiforgery] - ValidateRequestAsync: Should throw AntiforgeryValidationException on failure")]
    public async Task ValidateRequestAsync_OnInvalid_ShouldThrow()
    {
        IHttpAntiforgery antiforgery = HttpAntiforgery.Create();
        TestHttpContext post = new(HttpMethod.Post); // no tokens

        await Should.ThrowAsync<AntiforgeryValidationException>(
            async () => await antiforgery.ValidateRequestAsync(post));
    }

    [Fact(DisplayName = "Cohesion Test [Http.Antiforgery] - ValidateRequestAsync: Should complete silently on success")]
    public async Task ValidateRequestAsync_OnValid_ShouldComplete()
    {
        HttpAntiforgeryOptions options = new();
        IHttpAntiforgery antiforgery = HttpAntiforgery.Create(options);
        HttpAntiforgeryTokenSet tokens = antiforgery.GetAndStoreTokens(new TestHttpContext(HttpMethod.Get));

        TestHttpContext post = new(HttpMethod.Post);
        post.SetRequestCookie(options.CookieName, tokens.CookieToken!);
        post.SetRequestHeader(options.HeaderName, tokens.RequestToken!);

        await Should.NotThrowAsync(async () => await antiforgery.ValidateRequestAsync(post));
    }

    [Fact(DisplayName = "Cohesion Test [Http.Antiforgery] - GetAndStoreTokens: Should set the cookie token and anti-caching headers")]
    public void GetAndStoreTokens_OnNewExchange_ShouldSetCookieAndSecurityHeaders()
    {
        HttpAntiforgeryOptions options = new();
        IHttpAntiforgery antiforgery = HttpAntiforgery.Create(options);
        TestHttpContext context = new(HttpMethod.Get);

        HttpAntiforgeryTokenSet tokens = antiforgery.GetAndStoreTokens(context);

        // The cookie token is queued on the response.
        bool cookieStored = false;
        foreach (HttpCookie cookie in context.Response.Cookies)
        {
            if (cookie.Name == options.CookieName && cookie.Value == tokens.CookieToken)
            {
                cookie.Options.HttpOnly.ShouldBeTrue();
                cookieStored = true;
            }
        }
        cookieStored.ShouldBeTrue();

        // Anti-caching + framing headers are applied.
        context.Response.Headers["Cache-Control"].Value.ShouldContain("no-cache");
        context.Response.Headers["Pragma"].Value.ShouldBe("no-cache");
        context.Response.Headers["X-Frame-Options"].Value.ShouldBe("SAMEORIGIN");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Antiforgery] - GetTokens: Should reuse an existing valid cookie token")]
    public void GetTokens_OnExistingValidCookie_ShouldReuseCookieToken()
    {
        HttpAntiforgeryOptions options = new();
        IHttpAntiforgery antiforgery = HttpAntiforgery.Create(options);

        HttpAntiforgeryTokenSet first = antiforgery.GetTokens(new TestHttpContext(HttpMethod.Get));

        TestHttpContext second = new(HttpMethod.Get);
        second.SetRequestCookie(options.CookieName, first.CookieToken!);
        HttpAntiforgeryTokenSet reused = antiforgery.GetTokens(second);

        reused.CookieToken.ShouldBe(first.CookieToken);
        // A fresh request token is minted, still bound to the same cookie.
        TestHttpContext post = new(HttpMethod.Post);
        post.SetRequestCookie(options.CookieName, reused.CookieToken!);
        post.SetRequestHeader(options.HeaderName, reused.RequestToken!);
    }

    private static string Tamper(string token)
    {
        char[] chars = token.ToCharArray();
        int last = chars.Length - 1;
        chars[last] = chars[last] == 'A' ? 'B' : 'A';
        return new string(chars);
    }
}
