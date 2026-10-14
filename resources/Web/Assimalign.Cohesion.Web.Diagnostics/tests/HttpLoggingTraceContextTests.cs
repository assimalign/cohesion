using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Logging;
using Assimalign.Cohesion.Web.Diagnostics.Tests.TestObjects;
using Assimalign.Cohesion.Web.Testing;

namespace Assimalign.Cohesion.Web.Diagnostics.Tests;

[Collection(nameof(LoggingTelemetryCollection))]
public class HttpLoggingTraceContextTests
{
    private const string serverSource = "Assimalign.Cohesion.Web.Server";
    private const string callerTrace = "0af7651916cd43dd8448eb211c80319c";
    private const string callerSpan = "b7ad6b7169203331";
    private const string traceParent = "00-" + callerTrace + "-" + callerSpan + "-01";

    [Theory(DisplayName = "Cohesion Test [Web.Diagnostics] - TraceContext: Log the exchange's server span with or without a caller or middleware activity")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task InvokeAsync_ServerSpan_ShouldLogExchangeIds(bool inboundParent, bool middlewareActivity)
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(30));
        using ActivityListener listener = Listen(ActivitySamplingResult.AllData);
        RecordingLoggerProvider recorded = new();
        using ILoggerFactory loggerFactory = new LoggerFactoryBuilder().AddProvider(recorded).Build();
        Activity? server = null;
        Activity? child = null;
        ActivityTraceId requestId = default;
        await using WebApplicationTestFactory factory = new();
        factory.Application
            .Use(async (context, next) =>
            {
                server = Activity.Current;
                requestId = context.Features.Get<IWebRequestIdFeature>()!.RequestId;
                context.Features.Get<IWebRequestActivityFeature>()!.Activity.ShouldBeSameAs(server);
                using Activity? nested = middlewareActivity ? new Activity("middleware").Start() : null;
                child = nested;
                await next(context);
            })
            .UseHttpLogging(loggerFactory)
            .Use((context, next) => Task.CompletedTask);
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = new(System.Net.Http.HttpMethod.Get, "/trace");
        if (inboundParent)
        {
            request.Headers.TryAddWithoutValidation("traceparent", traceParent).ShouldBeTrue();
        }

        // Act
        using HttpResponseMessage response = await client.SendAsync(request, cancellation.Token);

        // Assert
        response.EnsureSuccessStatusCode();
        server.ShouldNotBeNull();
        server.Kind.ShouldBe(ActivityKind.Server);
        server.Source.Name.ShouldBe(serverSource);
        ILoggerEntry entry = recorded.Entries.ShouldHaveSingleItem();
        entry.Attributes[HttpLoggingAttributes.TraceId].ShouldBe(requestId.ToHexString());
        entry.Attributes[HttpLoggingAttributes.TraceId].ShouldBe(server.TraceId.ToHexString());
        entry.Attributes[HttpLoggingAttributes.SpanId].ShouldBe(server.SpanId.ToHexString());
        server.SpanId.ToHexString().ShouldNotBe(callerSpan);
        if (inboundParent)
        {
            server.TraceId.ToHexString().ShouldBe(callerTrace);
            server.ParentSpanId.ToHexString().ShouldBe(callerSpan);
        }
        else
        {
            server.ParentSpanId.ShouldBe(default);
        }
        if (middlewareActivity)
        {
            child.ShouldNotBeNull().SpanId.ShouldNotBe(server.SpanId);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Web.Diagnostics] - TraceContext: An untraced exchange must not log an inherited same-source server activity")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvokeAsync_NoListenerUnderAmbientServerActivity_ShouldLogOnlyRequestId(bool inboundParent)
    {
        // Arrange — even the source, kind and trace id can match an unrelated ambient activity.
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(30));
        using ActivitySource source = new(serverSource);
        using ActivityListener listener = Listen(ActivitySamplingResult.AllData);
        using Activity ambient = source.StartActivity("ambient", ActivityKind.Server)!.ShouldNotBeNull();
        listener.Dispose();
        source.HasListeners().ShouldBeFalse();
        RecordingLoggerProvider recorded = new();
        using ILoggerFactory loggerFactory = new LoggerFactoryBuilder().AddProvider(recorded).Build();
        Activity? observedAmbient = null;
        ActivityTraceId requestId = default;
        await using WebApplicationTestFactory factory = new();
        factory.Application
            .UseHttpLogging(loggerFactory)
            .Use((context, next) =>
            {
                observedAmbient = Activity.Current;
                requestId = context.Features.Get<IWebRequestIdFeature>()!.RequestId;
                context.Features.Get<IWebRequestActivityFeature>()!.Activity.ShouldBeNull();
                return Task.CompletedTask;
            });
        using HttpClient client = factory.CreateClient();
        Activity.Current = null;
        using HttpRequestMessage request = new(System.Net.Http.HttpMethod.Get, "/ambient");
        if (inboundParent)
        {
            request.Headers.TryAddWithoutValidation("traceparent", ambient.Id).ShouldBeTrue();
        }

        // Act
        using HttpResponseMessage response = await client.SendAsync(request, cancellation.Token);

        // Assert
        response.EnsureSuccessStatusCode();
        observedAmbient.ShouldBeSameAs(ambient);
        requestId.ShouldNotBe(default);
        ILoggerEntry entry = recorded.Entries.ShouldHaveSingleItem();
        entry.Attributes[HttpLoggingAttributes.TraceId].ShouldBe(requestId.ToHexString());
        entry.Attributes.ContainsKey(HttpLoggingAttributes.SpanId).ShouldBeFalse();
        if (inboundParent)
        {
            requestId.ShouldBe(ambient.TraceId);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Web.Diagnostics] - TraceContext: A sampler declining the server span leaves only the request id")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvokeAsync_SamplerDeclines_ShouldLogOnlyRequestId(bool inboundParent)
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(30));
        using ActivityListener listener = Listen(ActivitySamplingResult.None);
        RecordingLoggerProvider recorded = new();
        using ILoggerFactory loggerFactory = new LoggerFactoryBuilder().AddProvider(recorded).Build();
        ActivityTraceId requestId = default;
        await using WebApplicationTestFactory factory = new();
        factory.Application.UseHttpLogging(loggerFactory).Use((context, next) =>
        {
            requestId = context.Features.Get<IWebRequestIdFeature>()!.RequestId;
            context.Features.Get<IWebRequestActivityFeature>()!.Activity.ShouldBeNull();
            return Task.CompletedTask;
        });
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = new(System.Net.Http.HttpMethod.Get, "/unsampled");
        if (inboundParent)
        {
            request.Headers.TryAddWithoutValidation("traceparent", traceParent).ShouldBeTrue();
        }

        // Act
        using HttpResponseMessage response = await client.SendAsync(request, cancellation.Token);

        // Assert
        response.EnsureSuccessStatusCode();
        ILoggerEntry entry = recorded.Entries.ShouldHaveSingleItem();
        entry.Attributes[HttpLoggingAttributes.TraceId].ShouldBe(requestId.ToHexString());
        entry.Attributes.ContainsKey(HttpLoggingAttributes.SpanId).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Diagnostics] - TraceContext: A custom server without correlation features must not log raw caller ids")]
    public async Task InvokeAsync_ServerOmitsFeatures_ShouldOmitTraceContext()
    {
        // Arrange
        RecordingLoggerProvider recorded = new();
        using ILoggerFactory loggerFactory = new LoggerFactoryBuilder().AddProvider(recorded).Build();
        TestPipelineBuilder builder = new();
        builder.UseHttpLogging(loggerFactory);
        LoggingTestContext context = new(new HttpConnectionInfo());
        context.Request.Headers[HttpHeaderKey.TraceParent] = traceParent;

        // Act
        await builder.LastMiddleware!.InvokeAsync(context, _ => Task.CompletedTask);

        // Assert
        ILoggerEntry entry = recorded.Entries.ShouldHaveSingleItem();
        entry.Attributes.ContainsKey(HttpLoggingAttributes.TraceId).ShouldBeFalse();
        entry.Attributes.ContainsKey(HttpLoggingAttributes.SpanId).ShouldBeFalse();
    }

    private static ActivityListener Listen(ActivitySamplingResult sampling)
    {
        ActivityListener listener = new()
        {
            ShouldListenTo = source => source.Name == serverSource,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) => sampling,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}
