using System;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Antiforgery.Tests;

public class HttpAntiforgeryTests
{
    [Theory(DisplayName = "Cohesion Test [Http.Antiforgery] - Create: Should return a concrete service usable through IHttpAntiforgery")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Create_OnEitherOverload_ShouldReturnHttpAntiforgeryUsableThroughInterface(bool useOptionsOverload)
    {
        // Arrange
        HttpAntiforgeryOptions options = new();
        TestHttpContext mint = new(HttpMethod.Get);
        TestHttpContext post = new(HttpMethod.Post);

        // Act
        HttpAntiforgery service = useOptionsOverload
            ? HttpAntiforgery.Create(options)
            : HttpAntiforgery.Create(configure =>
            {
                configure.CookieName = options.CookieName;
                configure.HeaderName = options.HeaderName;
            });
        IHttpAntiforgery antiforgery = service;
        HttpAntiforgeryTokenSet tokens = antiforgery.GetAndStoreTokens(mint);
        post.SetRequestCookie(options.CookieName, tokens.CookieToken!);
        post.SetRequestHeader(options.HeaderName, tokens.RequestToken!);
        bool valid = await antiforgery.IsRequestValidAsync(post);

        // Assert
        service.ShouldBeOfType<HttpAntiforgery>();
        valid.ShouldBeTrue();
    }

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

    [Theory(DisplayName = "Cohesion Test [Http.Antiforgery] - Validate: Repeated form tokens use the first value")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ValidateRequest_OnRepeatedFormToken_ShouldValidateOnlyFirstValue(bool firstIsValid)
    {
        // Arrange
        HttpAntiforgeryOptions options = new();
        IHttpAntiforgery antiforgery = HttpAntiforgery.Create(options);
        HttpAntiforgeryTokenSet tokens = antiforgery.GetTokens(new TestHttpContext(HttpMethod.Get));
        TestHttpContext post = new(HttpMethod.Post);
        post.SetRequestCookie(options.CookieName, tokens.CookieToken!);
        HttpFormCollection form = new();
        form.Add(options.FormFieldName, firstIsValid ? tokens.RequestToken! : "invalid-token");
        form.Add(options.FormFieldName, firstIsValid ? "invalid-token" : tokens.RequestToken!);
        post.Request.Form = form;

        // Act
        bool valid = await antiforgery.IsRequestValidAsync(post);

        // Assert
        valid.ShouldBe(firstIsValid);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Antiforgery] - Validate: An empty first form token falls back to the header")]
    public async Task ValidateRequest_OnEmptyFirstFormToken_ShouldUseHeaderToken()
    {
        // Arrange
        HttpAntiforgeryOptions options = new();
        IHttpAntiforgery antiforgery = HttpAntiforgery.Create(options);
        HttpAntiforgeryTokenSet tokens = antiforgery.GetTokens(new TestHttpContext(HttpMethod.Get));
        TestHttpContext post = new(HttpMethod.Post);
        post.SetRequestCookie(options.CookieName, tokens.CookieToken!);
        post.SetRequestHeader(options.HeaderName, tokens.RequestToken!);
        HttpFormCollection form = new();
        form.Add(options.FormFieldName, string.Empty);
        form.Add(options.FormFieldName, "invalid-token");
        post.Request.Form = form;

        // Act
        bool valid = await antiforgery.IsRequestValidAsync(post);

        // Assert
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

    [Fact(DisplayName = "Cohesion Test [Http.Antiforgery] - Validate: Should require a token for QUERY despite it being spec-safe (RFC 10008)")]
    public async Task ValidateRequest_OnQueryWithoutToken_ShouldFail()
    {
        // RFC 10008 registers QUERY as safe + idempotent, but it carries a request body, so its
        // CSRF exposure parallels POST. Antiforgery deliberately does NOT exempt QUERY: a QUERY
        // request with no token is rejected (the decision recorded in HttpAntiforgery.IsSafeMethod).
        IHttpAntiforgery antiforgery = HttpAntiforgery.Create();
        TestHttpContext queryWithoutToken = new(HttpMethod.Query);

        (await antiforgery.IsRequestValidAsync(queryWithoutToken)).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Antiforgery] - Validate: Should accept a QUERY carrying a valid token pair")]
    public async Task ValidateRequest_OnQueryWithValidPair_ShouldPass()
    {
        HttpAntiforgeryOptions options = new();
        IHttpAntiforgery antiforgery = HttpAntiforgery.Create(options);
        HttpAntiforgeryTokenSet tokens = antiforgery.GetAndStoreTokens(new TestHttpContext(HttpMethod.Get));

        TestHttpContext query = new(HttpMethod.Query);
        query.SetRequestCookie(options.CookieName, tokens.CookieToken!);
        query.SetRequestHeader(options.HeaderName, tokens.RequestToken!);

        (await antiforgery.IsRequestValidAsync(query)).ShouldBeTrue();
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

    [Fact(DisplayName = "Cohesion Test [Http.Antiforgery] - GetAndStoreTokens: A non-essential cookie token is not marked essential")]
    public void GetAndStoreTokens_WhenCookieNotEssential_ShouldNotMarkCookieEssential()
    {
        // Arrange
        HttpAntiforgeryOptions options = new() { CookieIsEssential = false };
        IHttpAntiforgery antiforgery = HttpAntiforgery.Create(options);
        TestHttpContext context = new(HttpMethod.Get);

        // Act
        antiforgery.GetAndStoreTokens(context);

        // Assert
        bool cookieStored = false;
        foreach (HttpCookie cookie in context.Response.Cookies)
        {
            if (cookie.Name == options.CookieName)
            {
                cookie.Options.IsEssential.ShouldBeFalse();
                cookieStored = true;
            }
        }

        cookieStored.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Antiforgery] - GetAndStoreTokens: A plaintext request's cookie token is not marked Secure by default")]
    public void GetAndStoreTokens_HttpRequestByDefault_ShouldNotMarkCookieSecure()
    {
        // Arrange
        HttpAntiforgeryOptions options = new();
        IHttpAntiforgery antiforgery = HttpAntiforgery.Create(options);
        TestHttpContext context = new(HttpMethod.Get);

        // Act
        antiforgery.GetAndStoreTokens(context);

        // Assert
        StoredCookie(context, options.CookieName).Options.Secure.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Antiforgery] - GetAndStoreTokens: An HTTPS request's cookie token is marked Secure by default")]
    public void GetAndStoreTokens_HttpsRequestByDefault_ShouldMarkCookieSecure()
    {
        // Arrange
        HttpAntiforgeryOptions options = new();
        IHttpAntiforgery antiforgery = HttpAntiforgery.Create(options);
        TestHttpContext context = new(HttpMethod.Get);
        context.SetScheme(HttpScheme.Https);

        // Act
        antiforgery.GetAndStoreTokens(context);

        // Assert
        StoredCookie(context, options.CookieName).Options.Secure.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Antiforgery] - GetAndStoreTokens: A trusted proxy's forwarded https marks the cookie token Secure")]
    public void GetAndStoreTokens_ForwardedHttpsOverPlaintextHop_ShouldMarkCookieSecure()
    {
        // Arrange — the proxy-to-application hop is plaintext; the forwarded feature carries the client's https.
        HttpAntiforgeryOptions options = new();
        IHttpAntiforgery antiforgery = HttpAntiforgery.Create(options);
        TestHttpContext context = new(HttpMethod.Get);
        context.Features.Set<IHttpForwardedFeature>(new TestForwardedFeature(HttpScheme.Https));

        // Act
        antiforgery.GetAndStoreTokens(context);

        // Assert
        StoredCookie(context, options.CookieName).Options.Secure.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Antiforgery] - GetAndStoreTokens: CookieSecure marks the cookie token Secure on plaintext requests too")]
    public void GetAndStoreTokens_CookieSecureOnHttpRequest_ShouldMarkCookieSecure()
    {
        // Arrange
        HttpAntiforgeryOptions options = new() { CookieSecure = true };
        IHttpAntiforgery antiforgery = HttpAntiforgery.Create(options);
        TestHttpContext context = new(HttpMethod.Get);

        // Act
        antiforgery.GetAndStoreTokens(context);

        // Assert
        StoredCookie(context, options.CookieName).Options.Secure.ShouldBeTrue();
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
                cookie.Options.IsEssential.ShouldBeTrue(); // a consent policy must not drop it
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

    [Theory(DisplayName = "Cohesion Test [Http.Antiforgery] - Create: A CookiePath outside path-value fails when the service is created, not when a token is stored")]
    [InlineData("/tenant;x")] // ';' would add an attribute to the Set-Cookie line
    [InlineData("/café")] // not a US-ASCII CHAR
    [InlineData("/a\r\nb")] // CR LF
    public void Create_CookiePathOutsideGrammar_ShouldThrowAtCreation(string cookiePath)
    {
        // Arrange
        HttpAntiforgeryOptions options = new() { CookiePath = cookiePath };

        // Act
        ArgumentException fromOptions = Should.Throw<ArgumentException>(() => HttpAntiforgery.Create(options));
        ArgumentException fromConfigure = Should.Throw<ArgumentException>(
            () => HttpAntiforgery.Create(configure => configure.CookiePath = cookiePath));

        // Assert
        fromOptions.ParamName.ShouldBe("options");
        fromOptions.Message.ShouldContain("HttpAntiforgeryOptions.CookiePath");
        fromOptions.InnerException.ShouldBeOfType<ArgumentException>().ParamName.ShouldBe("Path");
        fromConfigure.Message.ShouldContain("HttpAntiforgeryOptions.CookiePath");
    }

    [Theory(DisplayName = "Cohesion Test [Http.Antiforgery] - Create: A CookiePath in path-value is accepted and reaches the cookie token")]
    [InlineData("/")]
    [InlineData("/app/v1")]
    [InlineData("/caf%C3%A9")] // the percent-encoded form of the refused '/café'
    public void Create_CookiePathInGrammar_ShouldStoreCookieTokenUnderPath(string cookiePath)
    {
        // Arrange
        HttpAntiforgeryOptions options = new() { CookiePath = cookiePath };
        IHttpAntiforgery antiforgery = HttpAntiforgery.Create(options);
        TestHttpContext context = new(HttpMethod.Get);

        // Act
        antiforgery.GetAndStoreTokens(context);

        // Assert
        StoredCookie(context, options.CookieName).Options.Path.ShouldBe(cookiePath);
    }

    private static HttpCookie StoredCookie(TestHttpContext context, string name)
    {
        foreach (HttpCookie cookie in context.Response.Cookies)
        {
            if (cookie.Name == name)
            {
                return cookie;
            }
        }

        throw new ShouldAssertException($"No '{name}' cookie was stored on the response.");
    }

    private static string Tamper(string token)
    {
        char[] chars = token.ToCharArray();
        int last = chars.Length - 1;
        chars[last] = chars[last] == 'A' ? 'B' : 'A';
        return new string(chars);
    }
}
