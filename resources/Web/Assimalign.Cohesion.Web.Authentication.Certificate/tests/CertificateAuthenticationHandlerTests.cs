using System;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Authentication.Certificate.Tests.TestObjects;

namespace Assimalign.Cohesion.Web.Authentication.Certificate.Tests;

public class CertificateAuthenticationHandlerTests
{
    [Theory(DisplayName = "Cohesion Test [Web.Authentication.Certificate] - Authenticate: No client certificate should yield no result")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Authenticate_WithoutCertificate_ShouldReturnNoResult(bool tls)
    {
        // Arrange
        TestHttpContext context = TestHttpContext.Create(tls: tls);
        CertificateAuthenticationOptions options = new()
        {
            OnCertificateValidated = (_, _) => throw new InvalidOperationException("No certificate can be validated."),
        };
        IAuthenticationHandler handler = await InitializeAsync(options, context);

        // Act
        AuthenticateResult result = await handler.AuthenticateAsync(CancellationToken.None);

        // Assert
        result.None.ShouldBeTrue();
        result.Succeeded.ShouldBeFalse();
        result.Failure.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication.Certificate] - Authenticate: Validated certificate should supply the default claims")]
    public async Task Authenticate_WithCertificate_ShouldCreateAuthenticatedPrincipal()
    {
        // Arrange
        using X509Certificate2 certificate = TestCertificates.CreateClient();
        IAuthenticationHandler handler = await InitializeAsync(new(), TestHttpContext.Create(certificate));

        // Act
        AuthenticateResult result = await handler.AuthenticateAsync(CancellationToken.None);

        // Assert
        result.Succeeded.ShouldBeTrue();
        ClaimsPrincipal principal = result.Principal.ShouldNotBeNull();
        principal.Identity.ShouldNotBeNull().IsAuthenticated.ShouldBeTrue();
        principal.Identity!.Name.ShouldBe(certificate.Subject);
        principal.Identity.AuthenticationType.ShouldBe(CertificateAuthenticationDefaults.AuthenticationScheme);
        principal.FindFirst(ClaimTypes.X500DistinguishedName).ShouldNotBeNull().Value.ShouldBe(certificate.Subject);
        principal.FindFirst(CertificateClaimTypes.Issuer).ShouldNotBeNull().Value.ShouldBe(certificate.Issuer);
        principal.FindFirst(ClaimTypes.Thumbprint).ShouldNotBeNull().Value.ShouldBe(certificate.Thumbprint);
        principal.FindFirst(ClaimTypes.SerialNumber).ShouldNotBeNull().Value.ShouldBe(certificate.SerialNumber);
        certificate.GetCertHash().ShouldNotBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication.Certificate] - Authenticate: Callback should receive the request and replace the principal")]
    public async Task Authenticate_WithMappingCallback_ShouldUseReplacementPrincipal()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(10));
        using X509Certificate2 certificate = TestCertificates.CreateClient();
        TestHttpContext httpContext = TestHttpContext.Create(certificate);
        ClaimsPrincipal mapped = new(new ClaimsIdentity([new Claim(ClaimTypes.Name, "alice"), new Claim(ClaimTypes.Role, "admin")], "mapped"));
        CertificateAuthenticationOptions options = new()
        {
            OnCertificateValidated = (context, token) =>
            {
                context.HttpContext.ShouldBeSameAs(httpContext);
                context.ClientCertificate.ShouldBeSameAs(certificate);
                context.Scheme.Name.ShouldBe(CertificateAuthenticationDefaults.AuthenticationScheme);
                token.ShouldBe(cancellation.Token);
                context.Principal = mapped;
                return ValueTask.CompletedTask;
            }
        };
        IAuthenticationHandler handler = await InitializeAsync(options, httpContext);

        // Act
        AuthenticateResult result = await handler.AuthenticateAsync(cancellation.Token);

        // Assert
        result.Principal.ShouldBeSameAs(mapped);
        result.Principal!.IsInRole("admin").ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication.Certificate] - Authenticate: Callback failure should remain a failure after principal replacement")]
    public async Task Authenticate_WithRejectedCertificate_ShouldKeepFirstFailure()
    {
        // Arrange
        using X509Certificate2 certificate = TestCertificates.CreateClient();
        CertificateAuthenticationOptions options = new()
        {
            OnCertificateValidated = (context, _) =>
            {
                context.Fail("account disabled");
                context.Fail("later rejection");
                context.Principal = new ClaimsPrincipal(new ClaimsIdentity("replacement"));
                return ValueTask.CompletedTask;
            }
        };
        IAuthenticationHandler handler = await InitializeAsync(options, TestHttpContext.Create(certificate));

        // Act
        AuthenticateResult result = await handler.AuthenticateAsync(CancellationToken.None);

        // Assert
        result.Succeeded.ShouldBeFalse();
        result.None.ShouldBeFalse();
        result.Failure.ShouldNotBeNull().Message.ShouldBe("account disabled");
        result.Principal.ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Web.Authentication.Certificate] - Authenticate: Callback without an authenticated identity should fail closed")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Authenticate_WithUnauthenticatedReplacement_ShouldFail(bool nullPrincipal)
    {
        // Arrange
        using X509Certificate2 certificate = TestCertificates.CreateClient();
        CertificateAuthenticationOptions options = new()
        {
            OnCertificateValidated = (context, _) =>
            {
                context.Principal = nullPrincipal ? null : new ClaimsPrincipal(new ClaimsIdentity());
                return ValueTask.CompletedTask;
            }
        };
        IAuthenticationHandler handler = await InitializeAsync(options, TestHttpContext.Create(certificate));

        // Act
        AuthenticateResult result = await handler.AuthenticateAsync(CancellationToken.None);

        // Assert
        result.Succeeded.ShouldBeFalse();
        result.None.ShouldBeFalse();
        result.Failure.ShouldNotBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication.Certificate] - Authenticate: Pending authentication should evaluate its callback only once")]
    public async Task Authenticate_ConcurrentCalls_ShouldReusePendingResult()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(10));
        using X509Certificate2 certificate = TestCertificates.CreateClient();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        CertificateAuthenticationOptions options = new()
        {
            OnCertificateValidated = async (_, token) =>
            {
                Interlocked.Increment(ref calls);
                await release.Task.WaitAsync(token);
            }
        };
        IAuthenticationHandler handler = await InitializeAsync(options, TestHttpContext.Create(certificate));

        // Act
        Task<AuthenticateResult> first = handler.AuthenticateAsync(cancellation.Token);
        Task<AuthenticateResult> second = handler.AuthenticateAsync(cancellation.Token);
        release.TrySetResult();
        AuthenticateResult[] results = await Task.WhenAll(first, second).WaitAsync(cancellation.Token);

        // Assert
        calls.ShouldBe(1);
        results[0].Succeeded.ShouldBeTrue();
        results[1].ShouldBeSameAs(results[0]);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication.Certificate] - Authenticate: Callback cancellation should propagate")]
    public async Task Authenticate_WhenCallbackIsCancelled_ShouldPropagateCancellation()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(10));
        using X509Certificate2 certificate = TestCertificates.CreateClient();
        CertificateAuthenticationOptions options = new()
        {
            OnCertificateValidated = (_, token) =>
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
                return ValueTask.CompletedTask;
            }
        };
        IAuthenticationHandler handler = await InitializeAsync(options, TestHttpContext.Create(certificate));

        // Act / Assert
        await Should.ThrowAsync<OperationCanceledException>(() => handler.AuthenticateAsync(cancellation.Token));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication.Certificate] - Authenticate: Application callback exception should propagate")]
    public async Task Authenticate_WhenCallbackThrows_ShouldPropagateException()
    {
        // Arrange
        using X509Certificate2 certificate = TestCertificates.CreateClient();
        InvalidOperationException expected = new("mapping unavailable");
        CertificateAuthenticationOptions options = new()
        {
            OnCertificateValidated = (_, _) => throw expected,
        };
        IAuthenticationHandler handler = await InitializeAsync(options, TestHttpContext.Create(certificate));

        // Act
        InvalidOperationException actual = await Should.ThrowAsync<InvalidOperationException>(() => handler.AuthenticateAsync(CancellationToken.None));

        // Assert
        actual.ShouldBeSameAs(expected);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication.Certificate] - CreateHandler: Later option changes should not replace the callback")]
    public async Task CreateHandler_WhenOriginalOptionsChange_ShouldUseSnapshot()
    {
        // Arrange
        using X509Certificate2 certificate = TestCertificates.CreateClient();
        CertificateAuthenticationOptions options = new()
        {
            OnCertificateValidated = (context, _) =>
            {
                context.Fail("original callback");
                return ValueTask.CompletedTask;
            }
        };
        IAuthenticationHandler handler = await InitializeAsync(options, TestHttpContext.Create(certificate));
        options.OnCertificateValidated = null;

        // Act
        AuthenticateResult result = await handler.AuthenticateAsync(CancellationToken.None);

        // Assert
        result.Failure.ShouldNotBeNull().Message.ShouldBe("original callback");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication.Certificate] - AddCertificate: Named registration should snapshot its options")]
    public async Task AddCertificate_WithNamedScheme_ShouldSnapshotRegistrationOptions()
    {
        // Arrange
        using X509Certificate2 certificate = TestCertificates.CreateClient();
        AuthenticationBuilder builder = new();
        CertificateAuthenticationOptions? captured = null;
        builder.AddCertificate("Partner", options =>
        {
            captured = options;
            options.DisplayName = "Partner certificate";
            options.OnCertificateValidated = (context, _) =>
            {
                context.Fail("registered callback");
                return ValueTask.CompletedTask;
            };
        });
        captured.ShouldNotBeNull().OnCertificateValidated = null;
        captured.DisplayName = "changed";
        AuthenticationScheme scheme = builder.Options.GetScheme("Partner").ShouldNotBeNull();
        IAuthenticationHandler handler = scheme.CreateHandler();
        await handler.InitializeAsync(scheme, TestHttpContext.Create(certificate), CancellationToken.None);

        // Act
        AuthenticateResult result = await handler.AuthenticateAsync(CancellationToken.None);

        // Assert
        scheme.DisplayName.ShouldBe("Partner certificate");
        result.Failure.ShouldNotBeNull().Message.ShouldBe("registered callback");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication.Certificate] - Challenge: Should issue a Certificate challenge")]
    public async Task Challenge_WithoutCertificate_ShouldRespondUnauthorized()
    {
        // Arrange
        TestHttpContext context = TestHttpContext.Create();
        IAuthenticationHandler handler = await InitializeAsync(new(), context);

        // Act
        await handler.ChallengeAsync(null, CancellationToken.None);

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        context.Response.Headers.GetValue(HttpHeaderKey.WWWAuthenticate).ShouldBe("Certificate");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication.Certificate] - Forbid: Should issue a bare forbidden response")]
    public async Task Forbid_WithAuthenticatedClient_ShouldRespondForbidden()
    {
        // Arrange
        TestHttpContext context = TestHttpContext.Create();
        IAuthenticationHandler handler = await InitializeAsync(new(), context);

        // Act
        await handler.ForbidAsync(null, CancellationToken.None);

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        context.Response.Headers.GetValue(HttpHeaderKey.WWWAuthenticate).ShouldBeNull();
    }

    private static async Task<IAuthenticationHandler> InitializeAsync(CertificateAuthenticationOptions options, TestHttpContext context)
    {
        IAuthenticationHandler handler = CertificateAuthentication.CreateHandler(options);
        await handler.InitializeAsync(new AuthenticationScheme(CertificateAuthenticationDefaults.AuthenticationScheme, null, () => handler), context, CancellationToken.None);
        return handler;
    }
}
