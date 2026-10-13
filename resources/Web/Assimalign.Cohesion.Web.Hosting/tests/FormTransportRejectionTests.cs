using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Http.Connections;
using Assimalign.Cohesion.Testing;
using Assimalign.Cohesion.Web.Antiforgery;
using Assimalign.Cohesion.Web.ErrorHandling;
using Assimalign.Cohesion.Web.Hosting.Tests.TestObjects;
using Assimalign.Cohesion.Web.Routing;
using Assimalign.Cohesion.Web.Testing;

namespace Assimalign.Cohesion.Web.Hosting.Tests;

/// <summary>Form readers leave malformed HTTP framing to the transport's rejection diagnostics.</summary>
[Collection(nameof(TelemetryCollection))]
public class FormTransportRejectionTests
{
    private const string transportSourceName = "Assimalign.Cohesion.Http.Connections";
    private const string formsSourceName = "Assimalign.Cohesion.Web.Forms";
    private const string antiforgerySourceName = "Assimalign.Cohesion.Web.Antiforgery";

    [Theory(DisplayName = "Cohesion Test [Web.Hosting] - Form middleware: Malformed chunk framing should increment only transport rejection diagnostics")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task InvokeAsync_MalformedChunkedForm_ShouldReportOnlyTransportRejection(bool useAntiforgery, bool useErrorHandling)
    {
        // Arrange — initialize every source through its public pipeline before recording baselines.
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(30));
        CancellationToken cancellationToken = cancellation.Token;
        await InitializeSourcesAsync(cancellationToken);
        using EventSourceRecorder transportEvents = CreateRecorder(transportSourceName);
        using EventSourceRecorder formEvents = CreateRecorder(formsSourceName);
        using EventSourceRecorder antiforgeryEvents = CreateRecorder(antiforgerySourceName);
        EventSourceRecorder.CounterSample malformedBefore = await formEvents.WaitForCounterAsync("malformed-forms", 0, cancellationToken);
        EventSourceRecorder.CounterSample limitBefore = await formEvents.WaitForCounterAsync("form-limit-rejections", 0, cancellationToken);
        EventSourceRecorder.CounterSample antiforgeryBefore = await antiforgeryEvents.WaitForCounterAsync("rejected-requests", 0, cancellationToken);
        long transportRejections = 0;
        using MeterListener meter = new()
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == transportSourceName && instrument.Name == "cohesion.http.server.rejected_requests")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        meter.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            bool http1 = false;
            bool badRequest = false;
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                http1 |= tag.Key == "network.protocol.version" && Equals(tag.Value, "1.1");
                badRequest |= tag.Key == "cohesion.http.rejection.status_code" && Equals(tag.Value, 400);
            }
            if (http1 && badRequest)
            {
                Interlocked.Add(ref transportRejections, value);
            }
        });
        meter.Start();
        long transportBefore = Interlocked.Read(ref transportRejections);
        int handlerCalls = 0;
        string requestId = string.Empty;
        HttpStatusCode? clientFault = null;
        TaskCompletionSource pipelineCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        await using InMemoryConnectionListener transport = new();
        await using WebApplicationTestFactory factory = new();
        factory.Builder.Server.UseServer(options => options.UseHttp1(transport));
        ConfigureFormServices(factory, useAntiforgery);
        factory.Application.Use(async (context, next) =>
        {
            requestId = context.Features.Get<IWebRequestIdFeature>()!.RequestId.ToHexString();
            try
            {
                await next(context);
            }
            finally
            {
                clientFault = context.Features.Get<IWebClientFaultFeature>()?.StatusCode;
                pipelineCompleted.TrySetResult();
            }
        });
        if (useErrorHandling)
        {
            factory.Application.UseErrorHandling();
        }
        ConfigureFormPipeline(factory, useAntiforgery, () => Interlocked.Increment(ref handlerCalls));
        await factory.StartAsync(cancellationToken);
        // Transport warnings are throttled process-wide; prior tests and warmup must leave the window.
        await Task.Delay(TimeSpan.FromMilliseconds(1100), cancellationToken);

        // Act — the head is valid, but Z is not a hexadecimal chunk size (RFC 9112 section 7.1).
        string response = await SendRawAsync(transport,
            "POST /form HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n" +
            "Content-Type: application/x-www-form-urlencoded\r\nTransfer-Encoding: chunked\r\n\r\nZ\r\nname=value\r\n0\r\n\r\n",
            cancellationToken);
        await pipelineCompleted.Task.WaitAsync(cancellationToken);
        await factory.StopAsync(cancellationToken);
        long formCompletedSequence = formEvents.CounterSequence;
        long antiforgeryCompletedSequence = antiforgeryEvents.CounterSequence;
        EventSourceRecorder.CounterSample malformedAfter = await formEvents.WaitForCounterAsync("malformed-forms", formCompletedSequence, cancellationToken);
        EventSourceRecorder.CounterSample limitAfter = await formEvents.WaitForCounterAsync("form-limit-rejections", formCompletedSequence, cancellationToken);
        EventSourceRecorder.CounterSample antiforgeryAfter = await antiforgeryEvents.WaitForCounterAsync("rejected-requests", antiforgeryCompletedSequence, cancellationToken);

        // Assert — the HTTP transport owns both the 400 response and its one rejection occurrence.
        response.ShouldStartWith("HTTP/1.1 400");
        clientFault.ShouldBe(HttpStatusCode.BadRequest);
        Volatile.Read(ref handlerCalls).ShouldBe(0);
        Interlocked.Read(ref transportRejections).ShouldBe(transportBefore + 1);
        EventWrittenEventArgs rejected = transportEvents.Events.Where(entry => entry.EventName == "RequestRejected").ShouldHaveSingleItem();
        Payload(rejected, "protocol").ShouldBe("1.1");
        Payload(rejected, "statusCode").ShouldBe(400);
        Payload(rejected, "streamId").ShouldBe(-1L);
        requestId.ShouldNotBeNullOrEmpty();
        malformedAfter.Value.ShouldBe(malformedBefore.Value);
        limitAfter.Value.ShouldBe(limitBefore.Value);
        antiforgeryAfter.Value.ShouldBe(antiforgeryBefore.Value);
        formEvents.Events.ShouldNotContain(entry => Equals(Payload(entry, "requestId"), requestId));
        antiforgeryEvents.Events.ShouldNotContain(entry => Equals(Payload(entry, "requestId"), requestId));
        transportEvents.Events.Concat(formEvents.Events).Concat(antiforgeryEvents.Events).ShouldNotContain(entry => entry.EventId == 0);
    }

    private static void ConfigureFormServices(WebApplicationTestFactory factory, bool useAntiforgery)
    {
        if (useAntiforgery)
        {
            factory.Builder.Services.AddRouting();
            factory.Builder.Services.AddAntiforgery();
        }
    }

    private static void ConfigureFormPipeline(WebApplicationTestFactory factory, bool useAntiforgery, Action handler)
    {
        if (useAntiforgery)
        {
            IRouterBuilder routes = factory.Application.UseRouting();
            factory.Application.UseAntiforgery();
            routes.Map(HttpMethod.Post, "/form", new RouterRouteHandler(context =>
            {
                handler();
                context.Response.StatusCode = HttpStatusCode.NoContent;
                return Task.CompletedTask;
            })).RequireAntiforgery();
        }
        else
        {
            factory.Application.UseForms();
            factory.Application.Use((context, _) =>
            {
                handler();
                context.Response.StatusCode = HttpStatusCode.NoContent;
                return Task.CompletedTask;
            });
        }
    }

    private static async Task InitializeSourcesAsync(CancellationToken cancellationToken)
    {
        // A form-level failure initializes Forms and Antiforgery even when framing faults correctly
        // bypass them. A rejected head initializes the transport without requiring internal access.
        foreach (bool useAntiforgery in new[] { false, true })
        {
            await using InMemoryConnectionListener transport = new();
            await using WebApplicationTestFactory factory = new();
            factory.Builder.Server.UseServer(options => options.UseHttp1(transport));
            ConfigureFormServices(factory, useAntiforgery);
            ConfigureFormPipeline(factory, useAntiforgery, () => { });
            await factory.StartAsync(cancellationToken);
            const string malformedForm = "--B\r\nsecret-malformed-header\r\n\r\nvalue\r\n--B--\r\n";
            string formResponse = await SendRawAsync(transport,
                "POST /form HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\nContent-Type: multipart/form-data; boundary=B\r\n" +
                $"Content-Length: {Encoding.ASCII.GetByteCount(malformedForm)}\r\n\r\n{malformedForm}",
                cancellationToken);
            formResponse.ShouldStartWith("HTTP/1.1 400");
            if (!useAntiforgery)
            {
                string transportResponse = await SendRawAsync(transport,
                    "GET / HTTP/1.1\r\nHost: localhost\r\nBad Name: value\r\n\r\n", cancellationToken);
                transportResponse.ShouldStartWith("HTTP/1.1 400");
            }
            await factory.StopAsync(cancellationToken);
        }
    }

    private static EventSourceRecorder CreateRecorder(string sourceName)
        => new(EventSource.GetSources().Single(source => source.Name == sourceName), EventLevel.Verbose, TimeSpan.FromMilliseconds(100));

    private static object? Payload(EventWrittenEventArgs entry, string name)
        => entry.PayloadNames?.IndexOf(name) is int index && index >= 0 ? entry.Payload![index] : null;

    private static async Task<string> SendRawAsync(InMemoryConnectionListener transport, string request, CancellationToken cancellationToken)
    {
        await using IDuplexConnection connection = await transport.CreateFactory().ConnectAsync(transport.EndPoint, cancellationToken);
        Stream stream = connection.AsStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), cancellationToken);
        await stream.FlushAsync(cancellationToken);
        StringBuilder response = new();
        byte[] buffer = new byte[1024];
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellationToken)) != 0)
        {
            response.Append(Encoding.ASCII.GetString(buffer, 0, count));
        }
        return response.ToString();
    }
}
