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
using Assimalign.Cohesion.Testing;
using Assimalign.Cohesion.Web.Internal;

using TestHttpContext = Assimalign.Cohesion.Web.Forms.Tests.UseFormsMiddlewareTests.TestHttpContext;
using StartedResponseStreamingFeature = Assimalign.Cohesion.Web.Forms.Tests.UseFormsMiddlewareTests.StartedResponseStreamingFeature;

namespace Assimalign.Cohesion.Web.Forms.Tests;

[Collection("WebFormsEventSource")]
public sealed class WebFormsEventSourceTests
{
    [Fact(DisplayName = "Cohesion Test [Web.Forms] - EventSource: provider name is the assembly name")]
    public void GetName_SourceType_ShouldMatchAssemblyName()
    {
        // Arrange / Act
        string name = EventSource.GetName(typeof(WebFormsEventSource));

        // Assert
        name.ShouldBe("Assimalign.Cohesion.Web.Forms");
        typeof(WebFormsEventSource).Namespace.ShouldBe("Assimalign.Cohesion.Web.Internal");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Forms] - EventSource: strict manifest accepts all event payloads")]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Test-only manifest validation retains the concrete source type.")]
    [UnconditionalSuppressMessage("Trimming", "IL2111", Justification = "Test-only manifest validation inspects the concrete source type.")]
    public void GenerateManifest_StrictValidation_ShouldSucceed()
    {
        // Arrange / Act
        string? manifest = EventSource.GenerateManifest(typeof(WebFormsEventSource), null, EventManifestOptions.Strict);

        // Assert
        manifest.ShouldNotBeNullOrEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Forms] - EventSource: real form rejections emit correlated events and exact totals")]
    public async Task InvokeAsync_RejectedForms_ShouldEmitDeclaredEventsOnce()
    {
        // Arrange
        var source = WebFormsEventSource.Log;
        using var recorder = new EventSourceRecorder(source, EventLevel.Verbose);
        await Task.Delay(TimeSpan.FromMilliseconds(1100));
        long limitsBefore = source.FormLimitRejections;
        long malformedBefore = source.MalformedForms;
        var limitContext = CreateContext("application/x-www-form-urlencoded", "secret-field=value");
        limitContext.Features.Set<IHttpFormFeature>(new HttpFormFeature(limitContext.Request, new HttpFormOptions { KeyLengthLimit = 3 }));
        var malformedContext = CreateContext("multipart/form-data; boundary=B", "--B\r\nsecret-malformed-header\r\n\r\nvalue\r\n--B--\r\n");
        malformedContext.Features.Set<IHttpResponseStreamingFeature>(new StartedResponseStreamingFeature());
        string limitId = RequestId(limitContext);
        string malformedId = RequestId(malformedContext);
        var middleware = new FormsMiddleware();

        // Act
        await middleware.InvokeAsync(limitContext, UnexpectedNext);
        await middleware.InvokeAsync(malformedContext, UnexpectedNext);

        // Assert
        EventWrittenEventArgs limitEvent = EventFor(recorder, 1, limitId);
        limitEvent.Level.ShouldBe(EventLevel.Verbose);
        limitEvent.PayloadNames.ShouldBe(["limit", "requestId"]);
        limitEvent.Payload!.ToArray().ShouldBe(new object?[] { "KeyLength", limitId });
        EventWrittenEventArgs malformedEvent = EventFor(recorder, 2, malformedId);
        malformedEvent.Level.ShouldBe(EventLevel.Verbose);
        malformedEvent.PayloadNames.ShouldBe(["exceptionType", "requestId"]);
        malformedEvent.Payload!.ToArray().ShouldBe(new object?[] { typeof(InvalidDataException).FullName, malformedId });
        EventWrittenEventArgs abortedEvent = EventFor(recorder, 3, malformedId);
        abortedEvent.Level.ShouldBe(EventLevel.Warning);
        abortedEvent.PayloadNames.ShouldBe(["statusCode", "suppressedCount", "requestId"]);
        abortedEvent.Payload![0].ShouldBe(400);
        source.FormLimitRejections.ShouldBe(limitsBefore + 1);
        source.MalformedForms.ShouldBe(malformedBefore + 1);
        limitContext.Response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        malformedContext.CancelRequested.ShouldBeTrue();
        recorder.Events.ShouldNotContain(item => item.EventId == 0);
        recorder.Events.SelectMany(item => item.Payload?.OfType<string>() ?? [])
            .ShouldNotContain(value => value.Contains("secret", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Forms] - EventSource: both counters publish after enable")]
    public async Task OnEventCommand_Enable_ShouldPublishCounters()
    {
        // Arrange
        using var recorder = new EventSourceRecorder(WebFormsEventSource.Log, EventLevel.Verbose, TimeSpan.FromMilliseconds(100));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Act
        await recorder.WaitForCountersAsync(["form-limit-rejections", "malformed-forms"], deadline.Token);

        // Assert
        recorder.Events.ShouldNotContain(item => item.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Forms] - EventSource: warnings throttle while rejection counters remain exact")]
    public async Task InvokeAsync_RepeatedCommittedRejections_ShouldThrottleWarningsAndCountEveryRejection()
    {
        // Arrange
        var source = WebFormsEventSource.Log;
        using var recorder = new EventSourceRecorder(source, EventLevel.Verbose);
        await Task.Delay(TimeSpan.FromMilliseconds(1100));
        var context = CreateContext();
        context.Features.Set<IHttpFormFeature>(new FailingFormFeature(new InvalidDataException("secret-form-value")));
        context.Features.Set<IHttpResponseStreamingFeature>(new StartedResponseStreamingFeature());
        string requestId = RequestId(context);
        long before = source.MalformedForms;
        var middleware = new FormsMiddleware();

        // Act
        for (int i = 0; i < 8; i++)
        {
            await middleware.InvokeAsync(context, UnexpectedNext);
        }
        EventWrittenEventArgs[] firstWindow = recorder.Events.Where(item => item.EventId == 3 && item.Payload!.Contains(requestId)).ToArray();
        await Task.Delay(TimeSpan.FromMilliseconds(1100));
        await middleware.InvokeAsync(context, UnexpectedNext);

        // Assert
        firstWindow.Length.ShouldBe(1);
        EventWrittenEventArgs[] warnings = recorder.Events.Where(item => item.EventId == 3 && item.Payload!.Contains(requestId)).ToArray();
        warnings.Length.ShouldBe(2);
        warnings[1].Payload![1].ShouldBe(7);
        source.MalformedForms.ShouldBe(before + 9);
        recorder.Events.Count(item => item.EventId == 2 && item.Payload!.Contains(requestId)).ShouldBe(9);
        recorder.Events.ShouldNotContain(item => item.EventId == 0);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Forms] - EventSource: a custom form limit never publishes its exception message")]
    [InlineData(false, "KeyLength")]
    [InlineData(true, "Unspecified")]
    public async Task InvokeAsync_CustomLimitMessage_ShouldWriteOnlyIndependentLimit(bool legacyConstructor, string expectedLimit)
    {
        // Arrange
        const string secret = "secret-field=value&cookie=credential";
        HttpFormLimitExceededException limit = legacyConstructor
            ? new HttpFormLimitExceededException(secret)
            : new HttpFormLimitExceededException(HttpFormLimit.KeyLength, secret);
        var context = CreateContext();
        context.Features.Set<IHttpFormFeature>(new FailingFormFeature(new InvalidDataException(secret, limit)));
        using var recorder = new EventSourceRecorder(WebFormsEventSource.Log, EventLevel.Verbose);

        // Act
        await new FormsMiddleware().InvokeAsync(context, UnexpectedNext);

        // Assert
        EventFor(recorder, 1, RequestId(context)).Payload![0].ShouldBe(expectedLimit);
        recorder.Events.SelectMany(item => item.Payload?.OfType<string>() ?? []).ShouldNotContain(secret);
        recorder.Events.ShouldNotContain(item => item.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Forms] - EventSource: disabled events retain exact counters without reading request ids")]
    public async Task InvokeAsync_NoListener_ShouldCountWithoutReadingRequestId()
    {
        // Arrange
        var source = WebFormsEventSource.Log;
        source.IsEnabled().ShouldBeFalse();
        long limitsBefore = source.FormLimitRejections;
        long malformedBefore = source.MalformedForms;
        var limitContext = new TestHttpContext();
        limitContext.Features.Set<IWebRequestIdFeature>(new UnreadableRequestIdFeature());
        limitContext.Features.Set<IHttpFormFeature>(new FailingFormFeature(new InvalidDataException("secret", new HttpFormLimitExceededException(HttpFormLimit.ValueCount, "secret"))));
        limitContext.Features.Set<IHttpResponseStreamingFeature>(new StartedResponseStreamingFeature());
        var malformedContext = new TestHttpContext();
        malformedContext.Features.Set<IWebRequestIdFeature>(new UnreadableRequestIdFeature());
        malformedContext.Features.Set<IHttpFormFeature>(new FailingFormFeature(new InvalidDataException("secret")));
        var middleware = new FormsMiddleware();

        // Act
        await middleware.InvokeAsync(limitContext, UnexpectedNext);
        await middleware.InvokeAsync(malformedContext, UnexpectedNext);

        // Assert
        source.FormLimitRejections.ShouldBe(limitsBefore + 1);
        source.MalformedForms.ShouldBe(malformedBefore + 1);
    }

    private static TestHttpContext CreateContext(string? contentType = null, string content = "")
    {
        var context = new TestHttpContext(contentType, new MemoryStream(Encoding.UTF8.GetBytes(content)));
        context.Features.Set<IWebRequestIdFeature>(new RequestIdFeature());
        return context;
    }

    private static string RequestId(IHttpContext context) => context.Features.Get<IWebRequestIdFeature>()!.RequestId.ToHexString();

    private static EventWrittenEventArgs EventFor(EventSourceRecorder recorder, int eventId, string requestId)
        => recorder.Events.Single(item => item.EventId == eventId && item.Payload!.Contains(requestId));

    private static Task UnexpectedNext(IHttpContext context) => throw new InvalidOperationException("Rejected form reached downstream middleware.");

    private sealed class RequestIdFeature : IWebRequestIdFeature
    {
        public string Name => nameof(IWebRequestIdFeature);
        public ActivityTraceId RequestId { get; } = ActivityTraceId.CreateRandom();
    }

    private sealed class UnreadableRequestIdFeature : IWebRequestIdFeature
    {
        public string Name => nameof(IWebRequestIdFeature);
        public ActivityTraceId RequestId => throw new InvalidOperationException("A disabled event read requestId.");
    }

    private sealed class FailingFormFeature : IHttpFormFeature
    {
        private readonly InvalidDataException _exception;

        public FailingFormFeature(InvalidDataException exception) => _exception = exception;

        public string Name => nameof(IHttpFormFeature);
        public IHttpFormCollection? Form { get; set; }
        public Task<IHttpFormCollection> ReadFormAsync(CancellationToken cancellationToken = default)
            => Task.FromException<IHttpFormCollection>(_exception);
    }
}
