using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Security.DataProtection;
using Assimalign.Cohesion.Testing;
using Assimalign.Cohesion.Web.Antiforgery.Internal;
using Assimalign.Cohesion.Web.Routing;

namespace Assimalign.Cohesion.Web.Antiforgery.Tests;

[Collection("Antiforgery event source")]
public sealed class AntiforgeryEventSourceTests
{
    [Fact(DisplayName = "Cohesion Test [Web.Antiforgery] - EventSource: Name matches the assembly")]
    public void GetName_Source_ShouldMatchAssembly()
    {
        // Arrange / Act
        string name = EventSource.GetName(typeof(AntiforgeryEventSource));

        // Assert
        name.ShouldBe("Assimalign.Cohesion.Web.Antiforgery");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Antiforgery] - EventSource: Strict manifest validates")]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Test-only manifest validation reflects the known event source.")]
    [UnconditionalSuppressMessage("Trimming", "IL2111", Justification = "Test-only manifest validation reflects the known event source.")]
    public void GenerateManifest_Source_ShouldPassStrictValidation()
    {
        // Arrange / Act
        string? manifest = EventSource.GenerateManifest(typeof(AntiforgeryEventSource), null, EventManifestOptions.Strict);

        // Assert
        manifest.ShouldNotBeNullOrEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Antiforgery] - InvokeAsync: Real rejection and unprotect operations emit declared payloads")]
    public async Task InvokeAsync_RejectionLifecycle_ShouldEmitDeclaredPayloads()
    {
        // Arrange
        AntiforgeryEventSource source = AntiforgeryEventSource.Log;
        using EventSourceRecorder recorder = new(source, EventLevel.Verbose);
        AntiforgeryMiddleware middleware = CreateMiddleware();
        await using AntiforgeryTestContext context = CreateContext();
        RequestIdFeature requestId = new();
        context.Features.Set<IWebRequestIdFeature>(requestId);
        context.Features.Set<IWebEndpointFeature>(new EndpointFeature());
        IDataProtector dataProtector = DataProtectionProvider.Create(new InMemoryKeyRepository()).CreateProtector("events");
        DataProtectionAntiforgeryProtector protector = new(dataProtector);
        long before = source.RejectedRequests;

        // Act
        await middleware.InvokeAsync(context, _ => throw new InvalidOperationException("Rejected endpoint ran."));
        bool unprotected = protector.TryUnprotect(new byte[] { 1 }, out _);

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        source.RejectedRequests.ShouldBe(before + 1);
        EventWrittenEventArgs rejected = recorder.Events.Single(item => item.EventId == 1 && Payload(item, "requestId").Equals(requestId.Id.ToHexString()));
        rejected.PayloadNames!.ToArray().ShouldBe(new[] { "reason", "statusCode", "tokenLocation", "routeTemplate", "requestId" });
        Payload(rejected, "reason").ShouldBe("TokenInvalid");
        Payload(rejected, "statusCode").ShouldBe(400);
        Payload(rejected, "tokenLocation").ShouldBe("None");
        Payload(rejected, "routeTemplate").ShouldBe("/protected/{id}");
        unprotected.ShouldBeFalse();
        EventWrittenEventArgs unprotect = recorder.Events.Single(item => item.EventId == 3);
        unprotect.PayloadNames!.ToArray().ShouldBe(new[] { "exceptionType", "exceptionMessage", "innerExceptionType" });
        Payload(unprotect, "exceptionMessage").ShouldBe("The protected payload is malformed.");
        Payload(unprotect, "innerExceptionType").ShouldBe(string.Empty);
        recorder.Events.ShouldNotContain(item => item.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Antiforgery] - EventSource: Rejection counter publishes")]
    public async Task OnEventCommand_CountersEnabled_ShouldPublishDeclaredCounter()
    {
        // Arrange
        using EventSourceRecorder recorder = new(AntiforgeryEventSource.Log, EventLevel.Verbose, TimeSpan.FromMilliseconds(100));
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));

        // Act
        await recorder.WaitForCountersAsync(new[] { "rejected-requests" }, timeout.Token);

        // Assert
        recorder.Events.ShouldNotContain(item => item.EventId == 0);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Antiforgery] - InvokeAsync: Rejections identify the token source without exposing input")]
    [InlineData("Header")]
    [InlineData("Form")]
    public async Task InvokeAsync_InvalidToken_ShouldReportLocationWithoutToken(string tokenLocation)
    {
        // Arrange
        const string secret = "secret-antiforgery-token";
        AntiforgeryMiddleware middleware = CreateMiddleware();
        using EventSourceRecorder recorder = new(AntiforgeryEventSource.Log, EventLevel.Verbose);
        await using AntiforgeryTestContext context = tokenLocation == "Form"
            ? AntiforgeryTestContext.ForUrlEncodedForm(HttpMethod.Post, "__RequestVerificationToken=" + secret)
            : new AntiforgeryTestContext(HttpMethod.Post).WithHeader("X-CSRF-TOKEN", secret);
        RequestIdFeature requestId = Publish(context);

        // Act
        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        // Assert
        EventWrittenEventArgs rejected = recorder.Events.Single(item => item.EventId == 1 && Payload(item, "requestId").Equals(requestId.Id.ToHexString()));
        Payload(rejected, "reason").ShouldBe("TokenInvalid");
        Payload(rejected, "tokenLocation").ShouldBe(tokenLocation);
        recorder.Events.SelectMany(item => item.Payload!).OfType<string>().ShouldNotContain(item => item.Contains(secret, StringComparison.Ordinal));
    }

    [Theory(DisplayName = "Cohesion Test [Web.Antiforgery] - InvokeAsync: A parsed form token takes precedence over a valid header in rejection events")]
    [InlineData("__RequestVerificationToken")]
    [InlineData("custom-csrf-field")]
    public async Task InvokeAsync_ParsedFormTokenWithValidHeader_ShouldReportFormTokenLocation(string formFieldName)
    {
        // Arrange
        const string invalidFormToken = "invalid-form-token";
        AntiforgeryBuilder builder = new();
        builder.Options.FormFieldName = formFieldName;
        AntiforgeryFeature registration = (AntiforgeryFeature)builder.Build();
        AntiforgeryMiddleware middleware = new(registration);
        await using AntiforgeryTestContext issuingContext = new(HttpMethod.Get);
        HttpAntiforgeryTokenSet tokens = registration.Antiforgery.GetTokens(issuingContext);
        await using AntiforgeryTestContext context = AntiforgeryTestContext
            .ForUrlEncodedForm(HttpMethod.Post, formFieldName + "=" + invalidFormToken)
            .WithCookie(registration.Options.CookieName, tokens.CookieToken!)
            .WithHeader(registration.Options.HeaderName, tokens.RequestToken!);
        (await registration.Antiforgery.IsRequestValidAsync(context)).ShouldBeTrue();
        // UseForms ahead of antiforgery publishes this same cached form.
        IHttpFormCollection form = await context.ReadFormAsync(context.RequestCancelled);
        context.RequestBody.Position = 0;
        RequestIdFeature requestId = Publish(context);
        using EventSourceRecorder recorder = new(AntiforgeryEventSource.Log, EventLevel.Verbose);
        int endpointInvocations = 0;

        // Act
        await middleware.InvokeAsync(context, _ =>
        {
            endpointInvocations++;
            return Task.CompletedTask;
        });

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        endpointInvocations.ShouldBe(0);
        context.Request.Form.ShouldBeSameAs(form);
        context.RequestBody.Position.ShouldBe(0);
        EventWrittenEventArgs rejected = recorder.Events.Single(item => item.EventId == 1 && Payload(item, "requestId").Equals(requestId.Id.ToHexString()));
        Payload(rejected, "reason").ShouldBe("TokenInvalid");
        Payload(rejected, "tokenLocation").ShouldBe("Form");
        recorder.Events.ShouldNotContain(item => item.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Antiforgery] - InvokeAsync: Malformed multipart header text never reaches events")]
    public async Task InvokeAsync_MalformedForm_ShouldOmitExceptionText()
    {
        // Arrange
        const string secret = "secret-multipart-header-without-colon";
        AntiforgeryMiddleware middleware = CreateMiddleware();
        using EventSourceRecorder recorder = new(AntiforgeryEventSource.Log, EventLevel.Verbose);
        await using AntiforgeryTestContext context = new AntiforgeryTestContext(HttpMethod.Post,
            Encoding.UTF8.GetBytes("--boundary\r\n" + secret + "\r\n\r\nignored\r\n--boundary--\r\n"))
            .WithHeader("Content-Type", "multipart/form-data; boundary=boundary");
        RequestIdFeature requestId = Publish(context);

        // Act
        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        // Assert
        EventWrittenEventArgs rejected = recorder.Events.Single(item => item.EventId == 1 && Payload(item, "requestId").Equals(requestId.Id.ToHexString()));
        Payload(rejected, "reason").ShouldBe("FormMalformed");
        Payload(rejected, "tokenLocation").ShouldBe("Form");
        Payload(rejected, "statusCode").ShouldBe(400);
        rejected.PayloadNames!.ShouldNotContain("exceptionMessage");
        recorder.Events.SelectMany(item => item.Payload!).OfType<string>().ShouldNotContain(item => item.Contains(secret, StringComparison.Ordinal));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Antiforgery] - InvokeAsync: Configured form limits are classified separately")]
    public async Task InvokeAsync_FormOverLimit_ShouldReportFormTooLarge()
    {
        // Arrange
        AntiforgeryMiddleware middleware = CreateMiddleware();
        using EventSourceRecorder recorder = new(AntiforgeryEventSource.Log, EventLevel.Verbose);
        string form = string.Join("&", Enumerable.Range(0, new HttpFormOptions().ValueCountLimit + 1).Select(index => "key" + index + "=value"));
        await using AntiforgeryTestContext context = AntiforgeryTestContext.ForUrlEncodedForm(HttpMethod.Post, form);
        RequestIdFeature requestId = Publish(context);

        // Act
        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        // Assert
        EventWrittenEventArgs rejected = recorder.Events.Single(item => item.EventId == 1 && Payload(item, "requestId").Equals(requestId.Id.ToHexString()));
        Payload(rejected, "reason").ShouldBe("FormTooLarge");
        Payload(rejected, "statusCode").ShouldBe(413);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Antiforgery] - InvokeAsync: Re-entered exchange counts once without listeners or feature reads")]
    public async Task InvokeAsync_ReenteredWithoutListener_ShouldCountOnceAndAvoidDiagnosticReads()
    {
        // Arrange
        AntiforgeryEventSource source = AntiforgeryEventSource.Log;
        source.IsEnabled().ShouldBeFalse();
        AntiforgeryMiddleware middleware = CreateMiddleware();
        await using AntiforgeryTestContext context = CreateContext();
        RequestIdFeature requestId = new();
        EndpointFeature endpoint = new();
        context.Features.Set<IWebRequestIdFeature>(requestId);
        context.Features.Set<IWebEndpointFeature>(endpoint);
        long before = source.RejectedRequests;

        // Act
        await middleware.InvokeAsync(context, _ => Task.CompletedTask);
        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        // Assert
        source.RejectedRequests.ShouldBe(before + 1);
        requestId.Reads.ShouldBe(0);
        endpoint.Reads.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Antiforgery] - InvokeAsync: Aborts are throttled with exact rejection counts")]
    public async Task InvokeAsync_CommittedResponse_ShouldThrottleAbortsAndCountEveryRequest()
    {
        // Arrange
        AntiforgeryEventSource source = AntiforgeryEventSource.Log;
        AntiforgeryMiddleware middleware = CreateMiddleware();
        using EventSourceRecorder recorder = new(source, EventLevel.Verbose);
        await Task.Delay(TimeSpan.FromMilliseconds(1100));
        long before = source.RejectedRequests;
        string[] ids = new string[7];

        // Act
        for (int index = 0; index < ids.Length; index++)
        {
            if (index == ids.Length - 1)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(1100));
            }

            await using AntiforgeryTestContext context = CreateContext();
            RequestIdFeature requestId = new();
            context.Features.Set<IWebRequestIdFeature>(requestId);
            context.Features.Set<IHttpResponseStreamingFeature>(new FakeResponseStreamingFeature());
            ids[index] = requestId.Id.ToHexString();
            await middleware.InvokeAsync(context, _ => Task.CompletedTask);
            context.CancelRequested.ShouldBeTrue();
        }

        // Assert
        source.RejectedRequests.ShouldBe(before + ids.Length);
        EventWrittenEventArgs[] aborts = recorder.Events.Where(item => item.EventId == 2 && ids.Contains((string)Payload(item, "requestId"))).ToArray();
        aborts.Length.ShouldBe(2);
        aborts[0].PayloadNames!.ToArray().ShouldBe(new[] { "reason", "routeTemplate", "suppressedCount", "requestId" });
        Payload(aborts[0], "reason").ShouldBe("TokenInvalid");
        Payload(aborts[1], "suppressedCount").ShouldBe(5);
        recorder.Events.ShouldNotContain(item => item.EventId == 0);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Antiforgery] - TryUnprotect: Only fixed messages and repository inner exception types are emitted")]
    [InlineData("secret-provider-token", "", false)]
    [InlineData("The protected payload could not be verified.", "The protected payload could not be verified.", false)]
    [InlineData("The key repository could not be read to resolve the key that produced this payload.", "The key repository could not be read to resolve the key that produced this payload.", true)]
    public void TryUnprotect_ProviderFailure_ShouldSanitizeException(string message, string expectedMessage, bool repositoryFailure)
    {
        // Arrange
        using EventSourceRecorder recorder = new(AntiforgeryEventSource.Log, EventLevel.Verbose);
        DataProtectionException exception = new(message, new IOException("secret-repository-path"));
        DataProtectionAntiforgeryProtector protector = new(new FailingProtector(exception));

        // Act
        bool result = protector.TryUnprotect(new byte[] { 1 }, out byte[]? plaintext);

        // Assert
        result.ShouldBeFalse();
        plaintext.ShouldBeNull();
        EventWrittenEventArgs recorded = recorder.Events.Single(item => item.EventId == 3);
        Payload(recorded, "exceptionMessage").ShouldBe(expectedMessage);
        Payload(recorded, "innerExceptionType").ShouldBe(repositoryFailure ? typeof(IOException).FullName : string.Empty);
        recorded.Payload!.OfType<string>().ShouldNotContain(item => item.Contains("secret-", StringComparison.Ordinal));
    }

    private static object Payload(EventWrittenEventArgs item, string name)
        => item.Payload![item.PayloadNames!.IndexOf(name)]!;

    private static AntiforgeryMiddleware CreateMiddleware()
        => new((AntiforgeryFeature)new AntiforgeryBuilder().Build());

    private static AntiforgeryTestContext CreateContext()
    {
        AntiforgeryTestContext context = new(HttpMethod.Post);
        Publish(context);
        return context;
    }

    private static RequestIdFeature Publish(AntiforgeryTestContext context)
    {
        RequestIdFeature requestId = new();
        context.Features.Set<IRouteMatchFeature>(new FakeRouteMatchFeature(AntiforgeryMetadata.Required));
        context.Features.Set<IWebRequestIdFeature>(requestId);
        return requestId;
    }

    private sealed class RequestIdFeature : IWebRequestIdFeature
    {
        public ActivityTraceId Id { get; } = ActivityTraceId.CreateRandom();
        public string Name => nameof(IWebRequestIdFeature);
        public int Reads { get; private set; }
        public ActivityTraceId RequestId
        {
            get
            {
                Reads++;
                return Id;
            }
        }
    }

    private sealed class EndpointFeature : IWebEndpointFeature
    {
        public string Name => nameof(IWebEndpointFeature);
        public WebApplicationMiddleware Endpoint => _ => Task.CompletedTask;
        public int Reads { get; private set; }
        public string RouteTemplate
        {
            get
            {
                Reads++;
                return "/protected/{id}";
            }
        }
    }

    private sealed class FailingProtector : IDataProtector
    {
        private readonly DataProtectionException _exception;

        public FailingProtector(DataProtectionException exception) => _exception = exception;

        public IDataProtector CreateProtector(string purpose) => this;
        public byte[] Protect(ReadOnlySpan<byte> plaintext) => throw new NotSupportedException();
        public byte[] Unprotect(ReadOnlySpan<byte> protectedData) => throw _exception;
    }
}
