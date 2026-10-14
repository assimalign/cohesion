using System;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Testing;
using Assimalign.Cohesion.Web.Authentication.Certificate.Internal;
using Assimalign.Cohesion.Web.Authentication.Certificate.Tests.TestObjects;

namespace Assimalign.Cohesion.Web.Authentication.Certificate.Tests;

[Collection(nameof(CertificateEventSourceCollection))]
public class CertificateAuthenticationEventSourceTests
{
    [Fact(DisplayName = "Cohesion Test [Web.Authentication.Certificate] - EventSource: Should use the assembly name")]
    public void GetName_CertificateAuthenticationEventSource_ShouldEqualAssemblyName()
    {
        // Act
        string name = EventSource.GetName(typeof(CertificateAuthenticationEventSource));

        // Assert
        name.ShouldBe(typeof(CertificateAuthenticationEventSource).Assembly.GetName().Name);
        name.ShouldBe("Assimalign.Cohesion.Web.Authentication.Certificate");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication.Certificate] - EventSource: Should generate a strict manifest")]
    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode", Justification = "Test-only manifest validation reflects over the event source; test assemblies are not trimmed.")]
    [UnconditionalSuppressMessage("Trimming", "IL2111:DynamicallyAccessedMembers", Justification = "Test-only manifest validation reflects over the event source; test assemblies are not trimmed.")]
    public void GenerateManifest_StrictMode_ShouldSucceed()
    {
        // Act
        string? manifest = EventSource.GenerateManifest(typeof(CertificateAuthenticationEventSource), null, EventManifestOptions.Strict);

        // Assert
        manifest.ShouldNotBeNull();
        manifest.ShouldContain("CertificateRejected", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication.Certificate] - Rejections: Should report bounded reasons and request ids once")]
    public async Task CertificateRejected_LocalFailures_ShouldReportOnlyDeclaredPayloads()
    {
        // Arrange
        using X509Certificate2 certificate = TestCertificates.CreateClient();
        using EventSourceRecorder recorder = new(CertificateAuthenticationEventSource.Log, EventLevel.Verbose);
        long before = CertificateAuthenticationEventSource.Log.RejectedCertificates;
        TestHttpContext missingContext = TestHttpContext.Create();
        TestRequestIdFeature missingId = InstallRequestId(missingContext);
        IAuthenticationHandler missing = await InitializeAsync(new(), missingContext, "missing");
        TestHttpContext rejectedContext = TestHttpContext.Create(certificate);
        TestRequestIdFeature rejectedId = InstallRequestId(rejectedContext);
        IAuthenticationHandler rejected = await InitializeAsync(new()
        {
            OnCertificateValidated = (context, _) =>
            {
                context.Fail("private callback text " + certificate.Subject + certificate.Issuer + certificate.Thumbprint);
                return ValueTask.CompletedTask;
            },
        }, rejectedContext, "rejected");

        // Act
        await missing.AuthenticateAsync(CancellationToken.None);
        await missing.ChallengeAsync(null, CancellationToken.None);
        await missing.ChallengeAsync(null, CancellationToken.None);
        await rejected.AuthenticateAsync(CancellationToken.None);
        await rejected.AuthenticateAsync(CancellationToken.None);
        await rejected.ChallengeAsync(null, CancellationToken.None);

        // Assert
        CertificateAuthenticationEventSource.Log.RejectedCertificates.ShouldBe(before + 2);
        recorder.Events.ShouldNotContain(item => item.EventId == 0);
        EventWrittenEventArgs missingEvent = recorder.Events.Where(item => IsFor(item, missingId.Value)).ShouldHaveSingleItem();
        missingEvent.EventId.ShouldBe(1);
        missingEvent.Level.ShouldBe(EventLevel.Verbose);
        missingEvent.PayloadNames.ShouldBe(["scheme", "reason", "requestId"]);
        missingEvent.Payload.ShouldBe(new object?[] { "missing", "NoCertificate", missingId.Value });
        EventWrittenEventArgs rejectedEvent = recorder.Events.Where(item => IsFor(item, rejectedId.Value)).ShouldHaveSingleItem();
        rejectedEvent.PayloadNames.ShouldBe(["scheme", "reason", "requestId"]);
        rejectedEvent.Payload.ShouldBe(new object?[] { "rejected", "ValidationFailed", rejectedId.Value });
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication.Certificate] - Counters: Should count without listeners and publish on enable")]
    public async Task Counters_DisabledThenEnabled_ShouldKeepExactRequestAndSchemeTotals()
    {
        // Arrange
        long before = CertificateAuthenticationEventSource.Log.RejectedCertificates;
        TestHttpContext context = TestHttpContext.Create();
        TestRequestIdFeature requestId = InstallRequestId(context);
        IAuthenticationHandler first = await InitializeAsync(new(), context, "first");
        IAuthenticationHandler second = await InitializeAsync(new(), context, "second");

        // Act
        await first.ChallengeAsync(null, CancellationToken.None);
        await first.ChallengeAsync(null, CancellationToken.None);
        await second.ChallengeAsync(null, CancellationToken.None);

        // Assert
        CertificateAuthenticationEventSource.Log.RejectedCertificates.ShouldBe(before + 2);
        requestId.Reads.ShouldBe(0);

        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(10));
        using EventSourceRecorder recorder = new(CertificateAuthenticationEventSource.Log, EventLevel.Verbose, TimeSpan.FromMilliseconds(100));
        await recorder.WaitForCountersAsync(["rejected-certificates"], cancellation.Token);
        CertificateAuthenticationEventSource.Log.RejectedCertificates.ShouldBe(before + 2);
        recorder.Events.ShouldNotContain(item => item.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication.Certificate] - Authenticate: Optional absence and successful mapping should not be rejections")]
    public async Task Authenticate_NoCredentialOrAcceptedCertificate_ShouldNotReportRejection()
    {
        // Arrange
        using X509Certificate2 certificate = TestCertificates.CreateClient();
        using EventSourceRecorder recorder = new(CertificateAuthenticationEventSource.Log, EventLevel.Verbose);
        long before = CertificateAuthenticationEventSource.Log.RejectedCertificates;
        TestHttpContext missingContext = TestHttpContext.Create();
        TestRequestIdFeature missingId = InstallRequestId(missingContext);
        IAuthenticationHandler missing = await InitializeAsync(new(), missingContext);
        TestHttpContext acceptedContext = TestHttpContext.Create(certificate);
        TestRequestIdFeature acceptedId = InstallRequestId(acceptedContext);
        IAuthenticationHandler accepted = await InitializeAsync(new(), acceptedContext);

        // Act
        AuthenticateResult none = await missing.AuthenticateAsync(CancellationToken.None);
        AuthenticateResult success = await accepted.AuthenticateAsync(CancellationToken.None);
        await accepted.ForbidAsync(null, CancellationToken.None);

        // Assert
        none.None.ShouldBeTrue();
        success.Succeeded.ShouldBeTrue();
        CertificateAuthenticationEventSource.Log.RejectedCertificates.ShouldBe(before);
        recorder.Events.ShouldNotContain(item => item.EventId == 0 || IsFor(item, missingId.Value) || IsFor(item, acceptedId.Value));
        missingId.Reads.ShouldBe(0);
        acceptedId.Reads.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Authentication.Certificate] - Authenticate: Invalid mapped principals should report validation failure")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Authenticate_UnauthenticatedReplacement_ShouldReportValidationFailure(bool nullPrincipal)
    {
        // Arrange
        using X509Certificate2 certificate = TestCertificates.CreateClient();
        using EventSourceRecorder recorder = new(CertificateAuthenticationEventSource.Log, EventLevel.Verbose);
        long before = CertificateAuthenticationEventSource.Log.RejectedCertificates;
        TestHttpContext context = TestHttpContext.Create(certificate);
        TestRequestIdFeature requestId = InstallRequestId(context);
        IAuthenticationHandler handler = await InitializeAsync(new()
        {
            OnCertificateValidated = (validation, _) =>
            {
                validation.Principal = nullPrincipal ? null : new ClaimsPrincipal(new ClaimsIdentity());
                return ValueTask.CompletedTask;
            },
        }, context);

        // Act
        AuthenticateResult result = await handler.AuthenticateAsync(CancellationToken.None);

        // Assert
        result.Failure.ShouldNotBeNull();
        CertificateAuthenticationEventSource.Log.RejectedCertificates.ShouldBe(before + 1);
        recorder.Events.Where(item => IsFor(item, requestId.Value)).ShouldHaveSingleItem().Payload![1].ShouldBe("ValidationFailed");
        recorder.Events.ShouldNotContain(item => item.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication.Certificate] - Authenticate: Pending validation and later challenges should count once")]
    public async Task Authenticate_PendingCallbackAndRepeatedChallenge_ShouldCountRejectionOnce()
    {
        // Arrange
        using X509Certificate2 certificate = TestCertificates.CreateClient();
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(10));
        using EventSourceRecorder recorder = new(CertificateAuthenticationEventSource.Log, EventLevel.Verbose);
        long before = CertificateAuthenticationEventSource.Log.RejectedCertificates;
        TestHttpContext context = TestHttpContext.Create(certificate);
        TestRequestIdFeature requestId = InstallRequestId(context);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int callbacks = 0;
        IAuthenticationHandler handler = await InitializeAsync(new()
        {
            OnCertificateValidated = async (validation, token) =>
            {
                Interlocked.Increment(ref callbacks);
                await release.Task.WaitAsync(token);
                validation.Fail("private rejection");
            },
        }, context);

        // Act
        Task<AuthenticateResult> first = handler.AuthenticateAsync(cancellation.Token);
        Task<AuthenticateResult> second = handler.AuthenticateAsync(cancellation.Token);
        release.SetResult();
        AuthenticateResult[] results = await Task.WhenAll(first, second).WaitAsync(cancellation.Token);
        context.Features.Set<IHttpTlsConnectionFeature>(new TestTlsConnectionFeature(null));
        await handler.ChallengeAsync(null, cancellation.Token);
        await handler.ChallengeAsync(null, cancellation.Token);

        // Assert
        callbacks.ShouldBe(1);
        results[0].ShouldBeSameAs(results[1]);
        CertificateAuthenticationEventSource.Log.RejectedCertificates.ShouldBe(before + 1);
        recorder.Events.Where(item => IsFor(item, requestId.Value)).ShouldHaveSingleItem().Payload![1].ShouldBe("ValidationFailed");
        recorder.Events.ShouldNotContain(item => item.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication.Certificate] - Authenticate: Callback exceptions and cancellation should not count as rejections")]
    public async Task Authenticate_CallbackFaultOrCancellation_ShouldPreserveFailureClassification()
    {
        // Arrange
        using X509Certificate2 certificate = TestCertificates.CreateClient();
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(10));
        using EventSourceRecorder recorder = new(CertificateAuthenticationEventSource.Log, EventLevel.Verbose);
        long before = CertificateAuthenticationEventSource.Log.RejectedCertificates;
        IAuthenticationHandler faulted = await InitializeAsync(new()
        {
            OnCertificateValidated = (_, _) => throw new InvalidOperationException("private application failure"),
        }, TestHttpContext.Create(certificate), "faulted");
        IAuthenticationHandler cancelled = await InitializeAsync(new()
        {
            OnCertificateValidated = (_, token) =>
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
                return ValueTask.CompletedTask;
            },
        }, TestHttpContext.Create(certificate), "cancelled");

        // Act / Assert
        await Should.ThrowAsync<InvalidOperationException>(() => faulted.AuthenticateAsync(CancellationToken.None));
        await Should.ThrowAsync<OperationCanceledException>(() => cancelled.AuthenticateAsync(cancellation.Token));
        CertificateAuthenticationEventSource.Log.RejectedCertificates.ShouldBe(before);
        recorder.Events.ShouldNotContain(item => item.EventId == 0 || item.Payload != null && (Equals(item.Payload[0], "faulted") || Equals(item.Payload[0], "cancelled")));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication.Certificate] - EventSource: Information level should not read request ids")]
    public async Task Challenge_InformationListener_ShouldCountWithoutReadingRequestId()
    {
        // Arrange
        using EventSourceRecorder recorder = new(CertificateAuthenticationEventSource.Log, EventLevel.Informational);
        long before = CertificateAuthenticationEventSource.Log.RejectedCertificates;
        TestHttpContext context = TestHttpContext.Create();
        TestRequestIdFeature requestId = InstallRequestId(context);
        IAuthenticationHandler handler = await InitializeAsync(new(), context, "information");

        // Act
        await handler.ChallengeAsync(null, CancellationToken.None);

        // Assert
        CertificateAuthenticationEventSource.Log.RejectedCertificates.ShouldBe(before + 1);
        requestId.Reads.ShouldBe(0);
        recorder.Events.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication.Certificate] - Initialize: A new request should reset the rejection guard")]
    public async Task Initialize_NewRequest_ShouldAllowAnotherRejection()
    {
        // Arrange
        long before = CertificateAuthenticationEventSource.Log.RejectedCertificates;
        IAuthenticationHandler handler = await InitializeAsync(new(), TestHttpContext.Create());

        // Act
        await handler.ChallengeAsync(null, CancellationToken.None);
        await handler.InitializeAsync(new AuthenticationScheme("next", null, () => handler), TestHttpContext.Create(), CancellationToken.None);
        await handler.ChallengeAsync(null, CancellationToken.None);

        // Assert
        CertificateAuthenticationEventSource.Log.RejectedCertificates.ShouldBe(before + 2);
    }

    private static TestRequestIdFeature InstallRequestId(TestHttpContext context)
    {
        TestRequestIdFeature feature = new();
        context.Features.Set<IWebRequestIdFeature>(feature);
        return feature;
    }

    private static bool IsFor(EventWrittenEventArgs item, string requestId)
        => item.EventId == 1 && item.Payload is { Count: 3 } && Equals(item.Payload[2], requestId);

    private static async Task<IAuthenticationHandler> InitializeAsync(CertificateAuthenticationOptions options, TestHttpContext context, string scheme = "certificate")
    {
        IAuthenticationHandler handler = CertificateAuthenticationHandler.Create(options);
        await handler.InitializeAsync(new AuthenticationScheme(scheme, null, () => handler), context, CancellationToken.None);
        return handler;
    }
}
