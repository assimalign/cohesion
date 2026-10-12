using System;
using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Testing;
using Assimalign.Cohesion.Web.Authorization;
using Assimalign.Cohesion.Web.Routing;
using Assimalign.Cohesion.Web.Testing;
using Assimalign.Cohesion.Web.Authentication.Bearer.Internal;
using Assimalign.Cohesion.Web.Authentication.Bearer.Tests.TestObjects;

namespace Assimalign.Cohesion.Web.Authentication.Bearer.Tests;

[Collection(nameof(EventSourceCollection))]
public sealed class JwtBearerEventSourceTests
{
    private static readonly DateTimeOffset _now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly byte[] _key = Encoding.UTF8.GetBytes("event-source-test-key-with-at-least-32-bytes");

    [Fact(DisplayName = "Cohesion Test [Web.Authentication.Bearer] - Source name matches its assembly")]
    public void GetName_Source_EqualsAssemblyName()
    {
        // Act + Assert
        EventSource.GetName(typeof(JwtBearerEventSource)).ShouldBe(typeof(JwtBearerEventSource).Assembly.GetName().Name);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication.Bearer] - Strict manifest is valid")]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Test-only manifest reflection; tests are not trimmed.")]
    [UnconditionalSuppressMessage("Trimming", "IL2111", Justification = "Test-only manifest reflection; tests are not trimmed.")]
    public void GenerateManifest_Strict_Succeeds()
    {
        // Act + Assert
        EventSource.GenerateManifest(typeof(JwtBearerEventSource), null, EventManifestOptions.Strict).ShouldNotBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Web.Authentication.Bearer] - Each rejection reports a bounded reason once")]
    [InlineData("EmptyCredential")]
    [InlineData("Malformed")]
    [InlineData("NotCompactJws")]
    [InlineData("UnsecuredAlgorithm")]
    [InlineData("SignatureEncoding")]
    [InlineData("NoMatchingKey")]
    [InlineData("SignatureInvalid")]
    [InlineData("ValidationFailed")]
    [InlineData("IssuerRejected")]
    [InlineData("AudienceRejected")]
    public async Task Authenticate_Rejection_ReportsReasonAndCountsOnce(string reason)
    {
        // Arrange
        using EventSourceRecorder recorder = new(JwtBearerEventSource.Log, EventLevel.Verbose);
        TestHttpContext context = TestHttpContext.Create();
        TestRequestIdFeature requestId = new();
        context.Features.Set<IWebRequestIdFeature>(requestId);
        string scheme = "reason-" + reason;
        string payload = TestJwt.Payload(_now, "issuer-secret", "audience-secret", name: "principal-secret");
        string token = reason switch
        {
            "EmptyCredential" => " ",
            "Malformed" => "malformed-token-secret",
            "NotCompactJws" => Compact("{}", payload, "AA"),
            "UnsecuredAlgorithm" => TestJwt.Unsecured(payload),
            "SignatureEncoding" => Compact("{\"alg\":\"HS256\"}", payload, "!"),
            "NoMatchingKey" => TestJwt.Hmac(_key, payload, keyId: "missing"),
            "SignatureInvalid" => TestJwt.Hmac(Encoding.UTF8.GetBytes("wrong-key"), payload, keyId: "current"),
            "ValidationFailed" => TestJwt.Hmac(_key, TestJwt.Payload(_now.AddDays(-1), "issuer-secret", "audience-secret"), keyId: "current"),
            "IssuerRejected" => TestJwt.Hmac(_key, TestJwt.Payload(_now, "other-issuer-secret", "audience-secret"), keyId: "current"),
            "AudienceRejected" => TestJwt.Hmac(_key, TestJwt.Payload(_now, "issuer-secret", "other-audience-secret"), keyId: "current"),
            _ => throw new InvalidOperationException(),
        };
        context.SetAuthorization("Bearer " + token);
        IAuthenticationHandler handler = JwtBearerAuthentication.CreateHandler(Options());
        await handler.InitializeAsync(new AuthenticationScheme(scheme, null, () => handler), context);
        long before = JwtBearerEventSource.Log.RejectedTokens;

        // Act
        AuthenticateResult first = await handler.AuthenticateAsync();
        AuthenticateResult repeated = await handler.AuthenticateAsync();
        await handler.ChallengeAsync(null);

        // Assert
        first.Succeeded.ShouldBeFalse();
        first.None.ShouldBeFalse();
        repeated.ShouldBeSameAs(first);
        JwtBearerEventSource.Log.RejectedTokens.ShouldBe(before + 1);
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
        EventWrittenEventArgs rejected = recorder.Events.Where(e => e.EventName == "TokenRejected" && Equals(e.Payload![0], scheme)).ShouldHaveSingleItem();
        rejected.EventId.ShouldBe(1);
        rejected.Level.ShouldBe(EventLevel.Verbose);
        rejected.PayloadNames.ShouldBe(["scheme", "reason", "algorithm", "keyId", "validationCode", "requestId"]);
        rejected.Payload![1].ShouldBe(reason);
        rejected.Payload[5].ShouldBe(requestId.RequestId.ToHexString());
        if (reason == "ValidationFailed")
        {
            rejected.Payload[4].ShouldBeOfType<string>().ShouldNotBeNullOrEmpty();
        }
        else
        {
            rejected.Payload[4].ShouldBe(string.Empty);
        }
        foreach (object? value in rejected.Payload)
        {
            string text = value?.ToString() ?? string.Empty;
            text.ShouldNotContain("secret", Case.Sensitive);
            text.ShouldNotContain("Bearer ", Case.Sensitive);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication.Bearer] - Attacker identifiers are truncated at Verbose")]
    public async Task Authenticate_LongAlgorithmAndKeyId_TruncatesPayload()
    {
        // Arrange
        using EventSourceRecorder recorder = new(JwtBearerEventSource.Log, EventLevel.Verbose);
        TestHttpContext context = TestHttpContext.Create();
        string algorithm = new('a', 80);
        string keyId = new('k', 300);
        context.SetAuthorization("Bearer " + Compact("{\"alg\":\"" + algorithm + "\",\"kid\":\"" + keyId + "\"}", "{}", "AA"));
        IAuthenticationHandler handler = JwtBearerAuthentication.CreateHandler(Options());
        const string scheme = "truncation";
        await handler.InitializeAsync(new AuthenticationScheme(scheme, null, () => handler), context);

        // Act
        await handler.AuthenticateAsync();

        // Assert
        EventWrittenEventArgs rejected = recorder.Events.Where(e => e.EventName == "TokenRejected" && Equals(e.Payload![0], scheme)).ShouldHaveSingleItem();
        rejected.Payload![1].ShouldBe("NoMatchingKey");
        rejected.Payload[2].ShouldBe(algorithm[..32]);
        rejected.Payload[3].ShouldBe(keyId[..128]);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication.Bearer] - Cached scheme handlers count independently across requests without listeners")]
    public async Task Authenticate_MultipleRequestsAndSchemes_CountsEachRejectionOnce()
    {
        // Arrange
        AuthenticationBuilder builder = new();
        builder.Options.DefaultScheme = "first";
        builder.AddScheme(new AuthenticationScheme("first", null, () => JwtBearerAuthentication.CreateHandler(Options())));
        builder.AddScheme(new AuthenticationScheme("second", null, () => JwtBearerAuthentication.CreateHandler(Options())));
        IAuthenticationService service = builder.Build();
        long before = JwtBearerEventSource.Log.RejectedTokens;

        // Act
        for (int i = 0; i < 2; i++)
        {
            TestHttpContext context = TestHttpContext.Create();
            context.SetAuthorization("Bearer malformed");
            await service.AuthenticateAsync(context, null);
            await service.AuthenticateAsync(context, "first");
            await service.AuthenticateAsync(context, "second");
            await service.AuthenticateAsync(context, "second");
        }

        // Assert
        JwtBearerEventSource.Log.RejectedTokens.ShouldBe(before + 4);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Authentication.Bearer] - Success and absent credentials produce no rejection")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Authenticate_NoRejection_DoesNotEmitOrCount(bool valid)
    {
        // Arrange
        using EventSourceRecorder recorder = new(JwtBearerEventSource.Log, EventLevel.Verbose);
        TestHttpContext context = TestHttpContext.Create();
        if (valid)
        {
            context.SetAuthorization("Bearer " + TestJwt.Hmac(_key, TestJwt.Payload(_now, "issuer-secret", "audience-secret"), keyId: "current"));
        }
        IAuthenticationHandler handler = JwtBearerAuthentication.CreateHandler(Options());
        await handler.InitializeAsync(new AuthenticationScheme("accepted", null, () => handler), context);
        long before = JwtBearerEventSource.Log.RejectedTokens;

        // Act
        AuthenticateResult result = await handler.AuthenticateAsync();

        // Assert
        result.Succeeded.ShouldBe(valid);
        result.None.ShouldBe(!valid);
        JwtBearerEventSource.Log.RejectedTokens.ShouldBe(before);
        recorder.Events.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Web.Authentication.Bearer] - Disabled Verbose events do not read request correlation")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Authenticate_VerboseDisabled_CountsWithoutReadingRequestId(bool informationListener)
    {
        // Arrange
        using EventSourceRecorder? recorder = informationListener ? new(JwtBearerEventSource.Log, EventLevel.Informational) : null;
        TestHttpContext context = TestHttpContext.Create();
        TestRequestIdFeature requestId = new();
        context.Features.Set<IWebRequestIdFeature>(requestId);
        context.SetAuthorization("Bearer malformed");
        IAuthenticationHandler handler = JwtBearerAuthentication.CreateHandler(Options());
        await handler.InitializeAsync(new AuthenticationScheme("disabled", null, () => handler), context);
        long before = JwtBearerEventSource.Log.RejectedTokens;

        // Act
        await handler.AuthenticateAsync();

        // Assert
        JwtBearerEventSource.Log.RejectedTokens.ShouldBe(before + 1);
        requestId.Reads.ShouldBe(0);
        recorder?.Events.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication.Bearer] - Authorization reauthentication increments the scheme counter once")]
    public async Task UseAuthorization_ReauthenticatesSameScheme_CountsOneRejection()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(30));
        using EventSourceRecorder recorder = new(JwtBearerEventSource.Log, EventLevel.Verbose);
        await using WebApplicationTestFactory factory = new();
        factory.Builder.Services.AddRouting();
        factory.Builder.Services.AddAuthentication(authentication =>
        {
            authentication.Options.DefaultScheme = "reauthentication";
            authentication.AddScheme(new AuthenticationScheme("reauthentication", null, () => JwtBearerAuthentication.CreateHandler(Options())));
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
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer private-invalid-token");
        long before = JwtBearerEventSource.Log.RejectedTokens;

        // Act — UseAuthentication evaluates the default, then Authorization selects that scheme again.
        using System.Net.Http.HttpResponseMessage response = await client.SendAsync(request, cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.Unauthorized);
        JwtBearerEventSource.Log.RejectedTokens.ShouldBe(before + 1);
        recorder.Events.Where(e => e.EventId == 1 && Equals(e.Payload![0], "reauthentication")
            && Equals(e.Payload![5], requestId)).ShouldHaveSingleItem();
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication.Bearer] - Rejection counter publishes")]
    public async Task Counters_Enabled_PublishesRejectedTokens()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(10));

        // Act
        using EventSourceRecorder recorder = new(JwtBearerEventSource.Log, EventLevel.Verbose, TimeSpan.FromMilliseconds(100));

        // Assert
        await recorder.WaitForCountersAsync(["rejected-tokens"], cancellation.Token);
    }

    private static JwtBearerOptions Options()
    {
        JwtBearerOptions options = new() { TimeProvider = new FixedTimeProvider(_now) };
        options.SigningKeys.Add(JwtSignatureVerifier.CreateHmac(_key, "current"));
        options.ValidIssuers.Add("issuer-secret");
        options.ValidAudiences.Add("audience-secret");
        return options;
    }

    private static string Compact(string header, string payload, string signature)
        => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(header)) + "." + Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payload)) + "." + signature;
}
