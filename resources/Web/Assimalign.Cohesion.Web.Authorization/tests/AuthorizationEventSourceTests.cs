using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Security.DataProtection;
using Assimalign.Cohesion.Testing;
using Assimalign.Cohesion.Web.Authentication;
using Assimalign.Cohesion.Web.Authentication.Bearer;
using Assimalign.Cohesion.Web.Authentication.Cookie;
using Assimalign.Cohesion.Web.Authorization.Internal;
using Assimalign.Cohesion.Web.Authorization.Tests.TestObjects;
using Assimalign.Cohesion.Web.Routing;
using Assimalign.Cohesion.Web.Testing;

using CohesionHttpMethod = Assimalign.Cohesion.Http.HttpMethod;
using NetHttpStatusCode = System.Net.HttpStatusCode;

namespace Assimalign.Cohesion.Web.Authorization.Tests;

[Collection(nameof(AuthorizationEventSourceCollection))]
public sealed class AuthorizationEventSourceTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(30);

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - EventSource: Should use its assembly name")]
    public void GetName_AuthorizationEventSource_ShouldEqualAssemblyName()
    {
        // Act
        string name = EventSource.GetName(typeof(AuthorizationEventSource));

        // Assert
        name.ShouldBe("Assimalign.Cohesion.Web.Authorization");
        name.ShouldBe(typeof(AuthorizationEventSource).Assembly.GetName().Name);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - EventSource: Should generate a strict manifest")]
    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode", Justification = "Test-only manifest validation; test assemblies are not trimmed.")]
    [UnconditionalSuppressMessage("Trimming", "IL2111:DynamicallyAccessedMembers", Justification = "Test-only manifest validation; test assemblies are not trimmed.")]
    public void GenerateManifest_StrictMode_ShouldSucceed()
    {
        // Act
        string? manifest = EventSource.GenerateManifest(typeof(AuthorizationEventSource), null, EventManifestOptions.Strict);

        // Assert
        manifest.ShouldNotBeNull().ShouldContain("Assimalign.Cohesion.Web.Authorization", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - EventSource: Challenge and forbid should share event 1 and identify the first failure")]
    public async Task UseAuthorization_DeniedRequests_ShouldReportBothOutcomesOnceWithoutPrivateValues()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        using EventSourceRecorder recorder = new(AuthorizationEventSource.Log, EventLevel.Verbose);
        long challengedBefore = AuthorizationEventSource.Log.ChallengedRequests;
        long forbiddenBefore = AuthorizationEventSource.Log.ForbiddenRequests;
        await using WebApplicationTestFactory factory = CreateFactory();
        IRouterBuilder routes = UseAuthorizedRouting(factory);
        routes.Map(CohesionHttpMethod.Get, "/private/{owner}", TestEndpoints.Ok())
            .RequireAuthorization(policy => policy
                .AddAuthenticationSchemes(TestEndpoints.Primary, TestEndpoints.Secondary)
                .RequireAuthenticatedUser()
                .RequireRole("private-required-role"));
        using HttpClient client = factory.CreateClient();
        string challengeId = ActivityTraceId.CreateRandom().ToHexString();
        string forbidId = ActivityTraceId.CreateRandom().ToHexString();
        string successId = ActivityTraceId.CreateRandom().ToHexString();
        using HttpRequestMessage anonymous = WithRequestId(new(HttpMethod.Get, "/private/private-owner?secret=private-query"), challengeId);
        using HttpRequestMessage signedIn = WithRequestId(
            TestEndpoints.Get("/private/private-owner", TestEndpoints.Primary, "private-user;role=private-actual-role;claim=private-type:private-value"), forbidId);
        using HttpRequestMessage admitted = WithRequestId(
            TestEndpoints.Get("/private/private-owner", TestEndpoints.Primary, "private-user;role=private-required-role"), successId);
        using Activity unrelated = new("unrelated");
        unrelated.SetIdFormat(ActivityIdFormat.W3C);
        unrelated.Start();

        // Act
        using HttpResponseMessage challenge = await client.SendAsync(anonymous, cancellation.Token);
        using HttpResponseMessage forbid = await client.SendAsync(signedIn, cancellation.Token);
        using HttpResponseMessage success = await client.SendAsync(admitted, cancellation.Token);

        // Assert
        challenge.StatusCode.ShouldBe(NetHttpStatusCode.Unauthorized);
        forbid.StatusCode.ShouldBe(NetHttpStatusCode.Forbidden);
        success.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        (AuthorizationEventSource.Log.ChallengedRequests - challengedBefore).ShouldBe(1);
        (AuthorizationEventSource.Log.ForbiddenRequests - forbiddenBefore).ShouldBe(1);
        EventWrittenEventArgs challenged = recorder.Events.Where(e => IsFor(e, challengeId)).ShouldHaveSingleItem();
        EventWrittenEventArgs forbidden = recorder.Events.Where(e => IsFor(e, forbidId)).ShouldHaveSingleItem();
        recorder.Events.ShouldNotContain(e => IsFor(e, successId));
        challenged.EventId.ShouldBe(1);
        forbidden.EventId.ShouldBe(1);
        challenged.EventName.ShouldBe("RequestDenied");
        forbidden.EventName.ShouldBe("RequestDenied");
        challenged.Level.ShouldBe(EventLevel.Verbose);
        challenged.PayloadNames.ShouldBe(["outcome", "routeTemplate", "policySource", "failedRequirement", "schemes", "requestId"]);
        forbidden.PayloadNames.ShouldBe(challenged.PayloadNames);
        challenged.Payload.ShouldBe(new object[] { "Challenge", "/private/{owner}", "Endpoint", "DenyAnonymousRequirement", "Primary, Secondary", challengeId });
        forbidden.Payload.ShouldBe(new object[] { "Forbid", "/private/{owner}", "Endpoint", "RolesRequirement", "Primary, Secondary", forbidId });
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
        string payloads = string.Join("|", recorder.Events.SelectMany(e => e.Payload?.OfType<string>() ?? Enumerable.Empty<string>()));
        foreach (string secret in new[] { "private-owner", "private-query", "private-user", "private-required-role", "private-actual-role", "private-type", "private-value" })
        {
            payloads.ShouldNotContain(secret, Case.Sensitive);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - EventSource: Both denial counters should publish")]
    public async Task Counters_Enabled_ShouldPublishEveryDeclaredCounter()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        using EventSourceRecorder recorder = new(AuthorizationEventSource.Log, EventLevel.Verbose, TimeSpan.FromMilliseconds(100));

        // Act
        await recorder.WaitForCountersAsync(["challenged-requests", "forbidden-requests"], cancellation.Token);

        // Assert
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Authorization] - EventSource: Fallback denials should identify their policy source")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UseAuthorization_FallbackPolicy_ShouldReportFallbackWithoutRequestPath(bool mappedEndpoint)
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        using EventSourceRecorder recorder = new(AuthorizationEventSource.Log, EventLevel.Verbose);
        await using WebApplicationTestFactory factory = CreateFactory(options => options.FallbackPolicy = options.DefaultPolicy);
        IRouterBuilder routes = UseAuthorizedRouting(factory);
        if (mappedEndpoint)
        {
            routes.Map(CohesionHttpMethod.Get, "/fallback/{id}", TestEndpoints.Ok());
        }

        using HttpClient client = factory.CreateClient();
        string requestId = ActivityTraceId.CreateRandom().ToHexString();
        using HttpRequestMessage request = WithRequestId(new(HttpMethod.Get, "/fallback/private-route-value"), requestId);

        // Act
        using HttpResponseMessage response = await client.SendAsync(request, cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.Unauthorized);
        EventWrittenEventArgs denied = recorder.Events.Where(e => IsFor(e, requestId)).ShouldHaveSingleItem();
        denied.Payload![1].ShouldBe(mappedEndpoint ? "/fallback/{id}" : string.Empty);
        denied.Payload[2].ShouldBe("Fallback");
        denied.Payload[4].ShouldBe(string.Empty);
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - EventSource: Counters should stay exact with no listener")]
    public async Task UseAuthorization_WithoutListener_ShouldCountDenialsAndIgnoreSuccess()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        long challengedBefore = AuthorizationEventSource.Log.ChallengedRequests;
        long forbiddenBefore = AuthorizationEventSource.Log.ForbiddenRequests;
        await using WebApplicationTestFactory factory = CreateFactory();
        IRouterBuilder routes = UseAuthorizedRouting(factory);
        routes.Map(CohesionHttpMethod.Get, "/guarded", TestEndpoints.Ok())
            .RequireAuthorization(policy => policy.RequireAuthenticatedUser().RequireRole("admin"));
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage reader = TestEndpoints.Get("/guarded", TestEndpoints.Primary, "reader");
        using HttpRequestMessage admin = TestEndpoints.Get("/guarded", TestEndpoints.Primary, "admin;role=admin");

        // Act
        using HttpResponseMessage challenge = await client.GetAsync("/guarded", cancellation.Token);
        using HttpResponseMessage forbid = await client.SendAsync(reader, cancellation.Token);
        using HttpResponseMessage success = await client.SendAsync(admin, cancellation.Token);

        // Assert
        challenge.StatusCode.ShouldBe(NetHttpStatusCode.Unauthorized);
        forbid.StatusCode.ShouldBe(NetHttpStatusCode.Forbidden);
        success.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        (AuthorizationEventSource.Log.ChallengedRequests - challengedBefore).ShouldBe(1);
        (AuthorizationEventSource.Log.ForbiddenRequests - forbiddenBefore).ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Authorization] - Policy: Internal failure index should preserve synchronous and asynchronous evaluation")]
    [InlineData(false, false, 0)]
    [InlineData(false, false, 1)]
    [InlineData(false, false, -1)]
    [InlineData(true, true, 0)]
    [InlineData(true, true, 1)]
    [InlineData(true, true, -1)]
    [InlineData(false, true, 1)]
    [InlineData(true, false, 1)]
    public async Task EvaluateFirstFailureAsync_RequirementOutcomes_ShouldReturnFirstIndexAndPreservePublicResult(
        bool firstAsynchronous, bool secondAsynchronous, int failureIndex)
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        AuthorizationPolicyBuilder builder = new();
        int evaluations = 0;
        for (int index = 0; index < 2; index++)
        {
            int requirementIndex = index;
            if (requirementIndex == 0 ? firstAsynchronous : secondAsynchronous)
            {
                builder.RequireAssertion(async (_, token) =>
                {
                    await Task.Yield();
                    token.ThrowIfCancellationRequested();
                    evaluations++;
                    return requirementIndex != failureIndex;
                });
            }
            else
            {
                builder.RequireAssertion(_ =>
                {
                    evaluations++;
                    return requirementIndex != failureIndex;
                });
            }
        }

        AuthorizationPolicy policy = builder.Build();
        AuthorizationContext context = new(TestHttpContext.Create(), new ClaimsPrincipal());

        // Act
        int result = await policy.EvaluateFirstFailureAsync(context, cancellation.Token);
        int diagnosticEvaluations = evaluations;
        bool publicResult = await policy.EvaluateAsync(context, cancellation.Token);

        // Assert
        result.ShouldBe(failureIndex);
        publicResult.ShouldBe(failureIndex < 0);
        diagnosticEvaluations.ShouldBe(failureIndex < 0 ? 2 : failureIndex + 1);
        evaluations.ShouldBe(diagnosticEvaluations * 2);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Authorization] - EventSource: Re-authenticating shipped handlers should report one rejection per request and scheme")]
    [InlineData(JwtBearerDefaults.AuthenticationScheme)]
    [InlineData(CookieAuthenticationDefaults.AuthenticationScheme)]
    public async Task UseAuthorization_ReauthenticatesBearerAndCookie_ShouldReportEachSchemeRejectionAndDenialOnce(string defaultScheme)
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        string keysDirectory = Path.Combine(Path.GetTempPath(), "cohesion-authorization-events", Guid.NewGuid().ToString("N"));
        try
        {
            await using WebApplicationTestFactory factory = new();
            factory.Builder.Services.AddRouting();
            factory.Builder.Services.AddAuthentication(authentication =>
            {
                authentication.Options.DefaultScheme = defaultScheme;
                authentication.UseDataProtection(DataProtectionProvider.Create(KeyRepository.CreateFileSystem(keysDirectory)));
                authentication.AddJwtBearer(options =>
                {
                    options.SigningKeys.Add(HmacJwtSignatureVerifier.Create(new byte[32]));
                    options.ValidIssuers.Add(TestJwt.Issuer);
                    options.ValidAudiences.Add(TestJwt.Audience);
                });
                authentication.AddCookie(options => options.CookieName = "EventTestCookie");
            });
            factory.Builder.Services.AddAuthorization();
            IRouterBuilder routes = UseAuthorizedRouting(factory);
            routes.Map(CohesionHttpMethod.Get, "/reauthenticate", TestEndpoints.Ok())
                .WithMetadata(ApiEndpointMetadata.Instance)
                .RequireAuthorization(policy => policy
                    .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme, CookieAuthenticationDefaults.AuthenticationScheme)
                    .RequireAuthenticatedUser());
            using HttpClient client = factory.CreateClient();
            using HttpRequestMessage warmup = RejectedCredentials(ActivityTraceId.CreateRandom().ToHexString());
            using HttpResponseMessage warmupResponse = await client.SendAsync(warmup, cancellation.Token);
            warmupResponse.StatusCode.ShouldBe(NetHttpStatusCode.Unauthorized);
            EventSource bearerSource = EventSource.GetSources().Single(source => source.Name == "Assimalign.Cohesion.Web.Authentication.Bearer");
            EventSource cookieSource = EventSource.GetSources().Single(source => source.Name == "Assimalign.Cohesion.Web.Authentication.Cookie");
            using EventSourceRecorder bearer = new(bearerSource, EventLevel.Verbose);
            using EventSourceRecorder cookie = new(cookieSource, EventLevel.Verbose);
            using EventSourceRecorder authorization = new(AuthorizationEventSource.Log, EventLevel.Verbose);
            long challengedBefore = AuthorizationEventSource.Log.ChallengedRequests;
            string requestId = ActivityTraceId.CreateRandom().ToHexString();
            using HttpRequestMessage request = RejectedCredentials(requestId);

            // Act — UseAuthentication authenticates the default; Authorization authenticates both again.
            using HttpResponseMessage response = await client.SendAsync(request, cancellation.Token);

            // Assert
            response.StatusCode.ShouldBe(NetHttpStatusCode.Unauthorized);
            EventWrittenEventArgs rejectedToken = bearer.Events.Where(e => IsFor(e, requestId)).ShouldHaveSingleItem();
            EventWrittenEventArgs rejectedTicket = cookie.Events.Where(e => IsFor(e, requestId)).ShouldHaveSingleItem();
            rejectedToken.EventName.ShouldBe("TokenRejected");
            rejectedTicket.EventName.ShouldBe("TicketRejected");
            Payload(rejectedToken, "scheme").ShouldBe(JwtBearerDefaults.AuthenticationScheme);
            Payload(rejectedTicket, "scheme").ShouldBe(CookieAuthenticationDefaults.AuthenticationScheme);
            authorization.Events.Where(e => IsFor(e, requestId)).ShouldHaveSingleItem().EventName.ShouldBe("RequestDenied");
            (AuthorizationEventSource.Log.ChallengedRequests - challengedBefore).ShouldBe(1);
            bearer.Events.Concat(cookie.Events).Concat(authorization.Events).ShouldNotContain(e => e.EventId == 0);
        }
        finally
        {
            if (Directory.Exists(keysDirectory))
            {
                Directory.Delete(keysDirectory, recursive: true);
            }
        }
    }

    private static HttpRequestMessage RejectedCredentials(string requestId)
    {
        HttpRequestMessage request = WithRequestId(new(HttpMethod.Get, "/reauthenticate"), requestId);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "private-invalid-token");
        request.Headers.TryAddWithoutValidation("Cookie", "EventTestCookie=!private-invalid-cookie!");
        return request;
    }

    private static HttpRequestMessage WithRequestId(HttpRequestMessage request, string requestId)
    {
        request.Headers.TryAddWithoutValidation("traceparent", $"00-{requestId}-0123456789abcdef-01");
        return request;
    }

    private static bool IsFor(EventWrittenEventArgs entry, string requestId)
        => entry.EventId > 0 && Equals(Payload(entry, "requestId"), requestId);

    private static object? Payload(EventWrittenEventArgs entry, string name)
    {
        int index = entry.PayloadNames?.IndexOf(name) ?? -1;
        return index < 0 ? null : entry.Payload![index];
    }

    private static WebApplicationTestFactory CreateFactory(Action<AuthorizationOptions>? configure = null)
    {
        WebApplicationTestFactory factory = new();
        factory.Builder.Services.AddRouting();
        factory.Builder.Services.AddAuthentication(authentication =>
        {
            authentication.Options.DefaultScheme = TestEndpoints.Primary;
            authentication.AddScheme(TestEndpoints.Scheme(TestEndpoints.Primary));
            authentication.AddScheme(TestEndpoints.Scheme(TestEndpoints.Secondary));
        });
        factory.Builder.Services.AddAuthorization(authorization => configure?.Invoke(authorization.Options));
        return factory;
    }

    private static IRouterBuilder UseAuthorizedRouting(WebApplicationTestFactory factory)
    {
        IRouterBuilder routes = factory.Application.UseRouting();
        factory.Application.UseAuthentication();
        factory.Application.UseAuthorization();
        return routes;
    }
}
