using System;
using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Security.DataProtection;
using Assimalign.Cohesion.Testing;
using Assimalign.Cohesion.Web.Authorization;
using Assimalign.Cohesion.Web.Routing;
using Assimalign.Cohesion.Web.Testing;
using Assimalign.Cohesion.Web.Authentication.Cookie.Internal;
using Assimalign.Cohesion.Web.Authentication.Cookie.Tests.TestObjects;

namespace Assimalign.Cohesion.Web.Authentication.Cookie.Tests;

[Collection(nameof(EventSourceCollection))]
public sealed class CookieAuthenticationEventSourceTests
{
    private static readonly DateTimeOffset _now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
    private const string cookieName = "diagnostics-cookie";

    [Fact(DisplayName = "Cohesion Test [Web.Authentication.Cookie] - Source name matches its assembly")]
    public void GetName_Source_EqualsAssemblyName()
    {
        // Act + Assert
        EventSource.GetName(typeof(CookieAuthenticationEventSource)).ShouldBe(typeof(CookieAuthenticationEventSource).Assembly.GetName().Name);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication.Cookie] - Strict manifest is valid")]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Test-only manifest reflection; tests are not trimmed.")]
    [UnconditionalSuppressMessage("Trimming", "IL2111", Justification = "Test-only manifest reflection; tests are not trimmed.")]
    public void GenerateManifest_Strict_Succeeds()
    {
        // Act + Assert
        EventSource.GenerateManifest(typeof(CookieAuthenticationEventSource), null, EventManifestOptions.Strict).ShouldNotBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Web.Authentication.Cookie] - Rejections report reason and count once per cached handler")]
    [InlineData("NotBase64Url")]
    [InlineData("UnprotectFailed")]
    [InlineData("PayloadMalformed")]
    [InlineData("Expired")]
    public async Task Authenticate_Rejection_ReportsReasonAndCountsOnce(string reason)
    {
        // Arrange
        using EventSourceRecorder recorder = new(CookieAuthenticationEventSource.Log, EventLevel.Verbose);
        TestHttpContext context = TestHttpContext.Create();
        TestRequestIdFeature requestId = new();
        context.Features.Set<IWebRequestIdFeature>(requestId);
        string value = reason == "NotBase64Url" ? "!cookie-secret!" : reason == "Expired" ? Ticket(_now.AddMinutes(-1)) : "AA";
        context.Request.Cookies.Add(new HttpCookie(cookieName, value));
        DataProtectionException? failure = reason == "UnprotectFailed" ? new DataProtectionException("The protected payload is malformed.") : null;
        IAuthenticationSignInHandler handler = CookieAuthenticationHandler.Create(Options(new DiagnosticDataProtector(failure)));
        string scheme = "reason-" + reason;
        await handler.InitializeAsync(new AuthenticationScheme(scheme, null, () => handler), context);
        long before = CookieAuthenticationEventSource.Log.RejectedTickets;

        // Act
        AuthenticateResult first = await handler.AuthenticateAsync();
        AuthenticateResult second = await handler.AuthenticateAsync();
        await handler.ChallengeAsync(null);

        // Assert
        first.Succeeded.ShouldBeFalse();
        first.None.ShouldBeFalse();
        second.ShouldBeSameAs(first);
        CookieAuthenticationEventSource.Log.RejectedTickets.ShouldBe(before + 1);
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
        EventWrittenEventArgs rejected = recorder.Events.Where(e => e.EventName == "TicketRejected" && Equals(e.Payload![0], scheme)).ShouldHaveSingleItem();
        rejected.EventId.ShouldBe(1);
        rejected.Level.ShouldBe(EventLevel.Verbose);
        rejected.PayloadNames.ShouldBe(["scheme", "reason", "exceptionType", "exceptionMessage", "innerExceptionType", "requestId"]);
        rejected.Payload![1].ShouldBe(reason);
        rejected.Payload[2].ShouldBe(reason == "Expired" ? string.Empty
            : reason == "UnprotectFailed" ? typeof(DataProtectionException).FullName : typeof(FormatException).FullName);
        rejected.Payload[3].ShouldBe(failure?.Message ?? string.Empty);
        rejected.Payload[4].ShouldBe(string.Empty);
        rejected.Payload[5].ShouldBe(requestId.RequestId.ToHexString());
        foreach (object? item in rejected.Payload)
        {
            (item?.ToString() ?? string.Empty).ShouldNotContain("secret", Case.Sensitive);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Web.Authentication.Cookie] - Only repository read failures expose inner exception type")]
    [InlineData("repository")]
    [InlineData("crypto")]
    [InlineData("custom")]
    public async Task Authenticate_UnprotectException_EmitsOnlySafeDiagnostics(string kind)
    {
        // Arrange
        const string repositoryMessage = "The key repository could not be read to resolve the key that produced this payload.";
        const string cryptoMessage = "The protected payload could not be verified.";
        DataProtectionException failure = kind switch
        {
            "repository" => new DataProtectionException(repositoryMessage, new IOException("repository-secret")),
            "crypto" => new DataProtectionException(cryptoMessage, new CryptographicException("crypto-secret")),
            _ => new DataProtectionException("custom-cookie-secret", new IOException("inner-secret")),
        };
        using EventSourceRecorder recorder = new(CookieAuthenticationEventSource.Log, EventLevel.Verbose);
        TestHttpContext context = TestHttpContext.Create();
        context.Request.Cookies.Add(new HttpCookie(cookieName, "AA"));
        IAuthenticationSignInHandler handler = CookieAuthenticationHandler.Create(Options(new DiagnosticDataProtector(failure)));
        await handler.InitializeAsync(new AuthenticationScheme(kind, null, () => handler), context);

        // Act
        await handler.AuthenticateAsync();

        // Assert
        EventWrittenEventArgs rejected = recorder.Events.Where(e => e.EventName == "TicketRejected" && Equals(e.Payload![0], kind)).ShouldHaveSingleItem();
        rejected.Payload![3].ShouldBe(kind == "custom" ? string.Empty : failure.Message);
        rejected.Payload[4].ShouldBe(kind == "repository" ? typeof(IOException).FullName : string.Empty);
        foreach (object? item in rejected.Payload)
        {
            (item?.ToString() ?? string.Empty).ShouldNotContain("secret", Case.Sensitive);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication.Cookie] - Cached scheme handlers count independently across requests without listeners")]
    public async Task Authenticate_MultipleRequestsAndSchemes_CountsEachRejectionOnce()
    {
        // Arrange
        AuthenticationBuilder builder = new();
        builder.Options.DefaultScheme = "first";
        builder.AddScheme(new AuthenticationScheme("first", null, () => CookieAuthenticationHandler.Create(Options(new DiagnosticDataProtector()))));
        builder.AddScheme(new AuthenticationScheme("second", null, () => CookieAuthenticationHandler.Create(Options(new DiagnosticDataProtector()))));
        IAuthenticationService service = builder.Build();
        long before = CookieAuthenticationEventSource.Log.RejectedTickets;

        // Act
        for (int i = 0; i < 2; i++)
        {
            TestHttpContext context = TestHttpContext.Create();
            context.Request.Cookies.Add(new HttpCookie(cookieName, "!cookie-secret!"));
            await service.AuthenticateAsync(context, null);
            await service.AuthenticateAsync(context, "first");
            await service.AuthenticateAsync(context, "second");
            await service.AuthenticateAsync(context, "second");
        }

        // Assert
        CookieAuthenticationEventSource.Log.RejectedTickets.ShouldBe(before + 4);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Authentication.Cookie] - Success and absent tickets produce no rejection")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Authenticate_NoRejection_DoesNotEmitOrCount(bool valid)
    {
        // Arrange
        using EventSourceRecorder recorder = new(CookieAuthenticationEventSource.Log, EventLevel.Verbose);
        TestHttpContext context = TestHttpContext.Create();
        if (valid)
        {
            context.Request.Cookies.Add(new HttpCookie(cookieName, Ticket(_now.AddHours(1))));
        }
        IAuthenticationSignInHandler handler = CookieAuthenticationHandler.Create(Options(new DiagnosticDataProtector()));
        await handler.InitializeAsync(new AuthenticationScheme("accepted", null, () => handler), context);
        long before = CookieAuthenticationEventSource.Log.RejectedTickets;

        // Act
        AuthenticateResult result = await handler.AuthenticateAsync();

        // Assert
        result.Succeeded.ShouldBe(valid);
        result.None.ShouldBe(!valid);
        CookieAuthenticationEventSource.Log.RejectedTickets.ShouldBe(before);
        recorder.Events.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Web.Authentication.Cookie] - Disabled Verbose events do not read request correlation")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Authenticate_VerboseDisabled_CountsWithoutReadingRequestId(bool informationListener)
    {
        // Arrange
        using EventSourceRecorder? recorder = informationListener ? new(CookieAuthenticationEventSource.Log, EventLevel.Informational) : null;
        TestHttpContext context = TestHttpContext.Create();
        TestRequestIdFeature requestId = new();
        context.Features.Set<IWebRequestIdFeature>(requestId);
        context.Request.Cookies.Add(new HttpCookie(cookieName, "!cookie-secret!"));
        IAuthenticationSignInHandler handler = CookieAuthenticationHandler.Create(Options(new DiagnosticDataProtector()));
        await handler.InitializeAsync(new AuthenticationScheme("disabled", null, () => handler), context);
        long before = CookieAuthenticationEventSource.Log.RejectedTickets;

        // Act
        await handler.AuthenticateAsync();

        // Assert
        CookieAuthenticationEventSource.Log.RejectedTickets.ShouldBe(before + 1);
        requestId.Reads.ShouldBe(0);
        recorder?.Events.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication.Cookie] - Authorization reauthentication increments the scheme counter once")]
    public async Task UseAuthorization_ReauthenticatesSameScheme_CountsOneRejection()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(30));
        using EventSourceRecorder recorder = new(CookieAuthenticationEventSource.Log, EventLevel.Verbose);
        await using WebApplicationTestFactory factory = new();
        factory.Builder.Services.AddRouting();
        factory.Builder.Services.AddAuthentication(authentication =>
        {
            authentication.Options.DefaultScheme = "reauthentication";
            authentication.AddScheme(new AuthenticationScheme("reauthentication", null, () => CookieAuthenticationHandler.Create(Options(new DiagnosticDataProtector()))));
        });
        factory.Builder.Services.AddAuthorization();
        IRouterBuilder routes = factory.Application.UseRouting();
        factory.Application.UseAuthentication();
        factory.Application.UseAuthorization();
        routes.Map(HttpMethod.Get, "/reauthenticate", new RouterRouteHandler(context => Task.CompletedTask))
            .WithMetadata(ApiEndpointMetadata.Instance)
            .RequireAuthorization(policy => policy.AddAuthenticationSchemes("reauthentication").RequireAuthenticatedUser());
        using System.Net.Http.HttpClient client = factory.CreateClient();
        using System.Net.Http.HttpRequestMessage request = new(System.Net.Http.HttpMethod.Get, "/reauthenticate");
        string requestId = Guid.NewGuid().ToString("N");
        request.Headers.TryAddWithoutValidation("traceparent", "00-" + requestId + "-0123456789abcdef-01");
        request.Headers.TryAddWithoutValidation("Cookie", cookieName + "=!private-invalid-cookie!");
        long before = CookieAuthenticationEventSource.Log.RejectedTickets;

        // Act — UseAuthentication evaluates the default, then Authorization selects that scheme again.
        using System.Net.Http.HttpResponseMessage response = await client.SendAsync(request, cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.Unauthorized);
        CookieAuthenticationEventSource.Log.RejectedTickets.ShouldBe(before + 1);
        recorder.Events.Where(e => e.EventId == 1 && Equals(e.Payload![0], "reauthentication")
            && Equals(e.Payload![5], requestId)).ShouldHaveSingleItem();
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication.Cookie] - Rejection counter publishes")]
    public async Task Counters_Enabled_PublishesRejectedTickets()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(10));

        // Act
        using EventSourceRecorder recorder = new(CookieAuthenticationEventSource.Log, EventLevel.Verbose, TimeSpan.FromMilliseconds(100));

        // Assert
        await recorder.WaitForCountersAsync(["rejected-tickets"], cancellation.Token);
    }

    private static CookieAuthenticationOptions Options(IDataProtector protector)
        => new() { TicketProtector = protector, CookieName = cookieName, TimeProvider = new MutableTimeProvider(_now), SlidingExpiration = false };

    private static string Ticket(DateTimeOffset expires)
    {
        ClaimsIdentity identity = new([new Claim(ClaimTypes.Name, "principal-secret")], "test");
        AuthenticationTicket ticket = new(new ClaimsPrincipal(identity), new AuthenticationProperties
        {
            ExpiresUtc = expires,
            RedirectUri = "/return-secret",
        }, "test");
        return Base64Url.EncodeToString(CookieTicketSerializer.Serialize(ticket));
    }
}
