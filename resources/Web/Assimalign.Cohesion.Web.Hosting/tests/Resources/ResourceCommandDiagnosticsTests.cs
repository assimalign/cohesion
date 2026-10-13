using System;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Testing;
using Assimalign.Cohesion.Web.Hosting.Internal;
using Assimalign.Cohesion.Web.Hosting.Resources;
using Assimalign.Cohesion.Web.Hosting.Tests.TestObjects;

namespace Assimalign.Cohesion.Web.Hosting.Tests;

[Collection(nameof(TelemetryCollection))]
public sealed class ResourceCommandDiagnosticsTests
{
    private const string displayPrefix = "Cohesion Test [Web.Hosting] - Command diagnostics: ";

    [Theory(DisplayName = displayPrefix + "default handler ArgumentException is reported by the producer once")]
    [InlineData("POST", "Apply")]
    [InlineData("DELETE", "Delete")]
    public async Task InvokeAsync_DefaultHandlerArgumentException_ShouldReportOnlyProducerFailure(string method, string operation)
    {
        // Arrange
        EventSource producer = await GetProducerSourceAsync();
        string kind = "registered." + Guid.NewGuid().ToString("N");
        IResourceControlPlane plane = ResourceControlPlane.Create([kind]);
        var handler = new ScriptedCommandHandler(kind);
        plane.RegisterCommandHandler(handler);
        ResourceCommand command = CreateCommand(kind);
        if (method == "DELETE")
        {
            await plane.ExecuteCommandAsync(command, CancellationToken.None);
        }
        handler.Failure = new ArgumentException("handler-secret payload-secret key-secret caller-secret");
        await Task.Delay(TimeSpan.FromMilliseconds(1100), CancellationToken.None);
        using var producerRecorder = new EventSourceRecorder(producer, EventLevel.Verbose);
        using var webRecorder = new EventSourceRecorder(WebHostingEventSource.Log, EventLevel.Verbose);
        await using var exchange = new ControlPlaneExchange("/cohesion/v1/commands", new HttpMethod(method));
        var requestId = new ControlPlaneRequestIdFeature();
        exchange.Features.Set<IWebRequestIdFeature>(requestId);
        WriteCommand(exchange, command);

        // Act
        await InvokeAsync(plane, exchange);

        // Assert
        EventWrittenEventArgs failure = producerRecorder.Events.Where(item => item.EventId == 5 &&
            Equals(Payload(item, "kind"), kind)).ShouldHaveSingleItem();
        producerRecorder.Events.Where(item => item.EventId is >= 2 and <= 5).ShouldHaveSingleItem().ShouldBeSameAs(failure);
        failure.EventName.ShouldBe("CommandHandlerFailed");
        Payload(failure, "operation").ShouldBe(operation);
        Payload(failure, "exceptionType").ShouldBe("System.ArgumentException");
        string.Join("|", failure.Payload!).ShouldNotContain("secret", Case.Sensitive);
        webRecorder.Events.ShouldNotContain(item => item.EventId == 3 &&
            Equals(Payload(item, "requestId"), requestId.ExpectedId));
        exchange.Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        producerRecorder.Events.ShouldNotContain(item => item.EventId == 0);
        webRecorder.Events.ShouldNotContain(item => item.EventId == 0);
    }

    [Theory(DisplayName = displayPrefix + "HTTP command outcomes belong only to the shared producer")]
    [InlineData("Applied", 2, 200)]
    [InlineData("Unknown", 3, 501)]
    [InlineData("Conflict", 4, 409)]
    public async Task InvokeAsync_CommandOutcome_ShouldAvoidDuplicateWebEvents(string outcome, int eventId, int statusCode)
    {
        // Arrange
        EventSource producer = await GetProducerSourceAsync();
        string kind = "registered." + Guid.NewGuid().ToString("N");
        IResourceControlPlane plane = ResourceControlPlane.Create([kind]);
        plane.RegisterCommandHandler(new ScriptedCommandHandler(kind));
        ResourceCommand command = CreateCommand(kind);
        if (outcome == "Conflict")
        {
            await plane.ExecuteCommandAsync(command, CancellationToken.None);
            command = command with { Owner = "other-owner-secret" };
        }
        else if (outcome == "Unknown")
        {
            command = command with { Kind = "unknown-kind-secret" };
        }
        await Task.Delay(TimeSpan.FromMilliseconds(1100), CancellationToken.None);
        using var producerRecorder = new EventSourceRecorder(producer, EventLevel.Verbose);
        using var webRecorder = new EventSourceRecorder(WebHostingEventSource.Log, EventLevel.Verbose);
        await using var exchange = new ControlPlaneExchange("/cohesion/v1/commands", HttpMethod.Post);
        var requestId = new ControlPlaneRequestIdFeature();
        exchange.Features.Set<IWebRequestIdFeature>(requestId);
        WriteCommand(exchange, command);

        // Act
        await InvokeAsync(plane, exchange);

        // Assert
        string expectedKind = outcome == "Unknown" ? string.Empty : kind;
        EventWrittenEventArgs reported = producerRecorder.Events.Where(item => item.EventId == eventId &&
            Equals(Payload(item, "kind"), expectedKind)).ShouldHaveSingleItem();
        producerRecorder.Events.Where(item => item.EventId is >= 2 and <= 5).ShouldHaveSingleItem().ShouldBeSameAs(reported);
        Payload(reported, "operation").ShouldBe("Apply");
        string.Join("|", reported.Payload!).ShouldNotContain("secret", Case.Sensitive);
        webRecorder.Events.ShouldNotContain(item => item.PayloadNames != null && item.PayloadNames.Contains("requestId") &&
            Equals(Payload(item, "requestId"), requestId.ExpectedId));
        exchange.Response.StatusCode.ShouldBe(new HttpStatusCode(statusCode));
        producerRecorder.Events.ShouldNotContain(item => item.EventId == 0);
        webRecorder.Events.ShouldNotContain(item => item.EventId == 0);
    }

    [Fact(DisplayName = displayPrefix + "a reused exception from another command does not hide a custom handler fault")]
    public async Task InvokeAsync_CustomPlaneReusesEarlierProducerException_ShouldReportWebHandlerFailure()
    {
        // Arrange: retain the original command so its weak marker remains alive throughout the test.
        EventSource producer = await GetProducerSourceAsync();
        string kind = "registered." + Guid.NewGuid().ToString("N");
        var exception = new ArgumentException("reused-exception-secret");
        IResourceControlPlane plane = ResourceControlPlane.Create([kind]);
        plane.RegisterCommandHandler(new ScriptedCommandHandler(kind) { Failure = exception });
        ResourceCommand originalCommand = CreateCommand(kind);
        ArgumentException originalFailure = await Should.ThrowAsync<ArgumentException>(async () =>
            await plane.ExecuteCommandAsync(originalCommand, CancellationToken.None));
        originalFailure.ShouldBeSameAs(exception);
        await Task.Delay(TimeSpan.FromMilliseconds(1100), CancellationToken.None);
        using var producerRecorder = new EventSourceRecorder(producer, EventLevel.Verbose);
        using var webRecorder = new EventSourceRecorder(WebHostingEventSource.Log, EventLevel.Verbose);
        await using var exchange = new ControlPlaneExchange("/cohesion/v1/commands", HttpMethod.Post);
        var requestId = new ControlPlaneRequestIdFeature();
        exchange.Features.Set<IWebRequestIdFeature>(requestId);
        WriteCommand(exchange, originalCommand);

        // Act: the identical envelope is deserialized into a different command instance.
        await InvokeAsync(new ReusedExceptionControlPlane(exception), exchange);

        // Assert
        EventWrittenEventArgs malformed = webRecorder.Events.Where(item => item.EventId == 3 &&
            Equals(Payload(item, "requestId"), requestId.ExpectedId)).ShouldHaveSingleItem();
        Payload(malformed, "phase").ShouldBe("Handler");
        Payload(malformed, "exceptionType").ShouldBe("System.ArgumentException");
        string.Join("|", malformed.Payload!).ShouldNotContain("secret", Case.Sensitive);
        producerRecorder.Events.ShouldNotContain(item => item.EventId >= 2 && item.EventId <= 5);
        exchange.Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        producerRecorder.Events.ShouldNotContain(item => item.EventId == 0);
        webRecorder.Events.ShouldNotContain(item => item.EventId == 0);
        GC.KeepAlive(originalCommand);
    }

    private static async Task<EventSource> GetProducerSourceAsync()
    {
        // Dispatch through the public control plane to initialize its source without a shipped IVT.
        const string kind = "diagnostics.warmup";
        IResourceControlPlane plane = ResourceControlPlane.Create([kind]);
        plane.RegisterCommandHandler(new ScriptedCommandHandler(kind));
        await plane.ExecuteCommandAsync(CreateCommand(kind), CancellationToken.None);
        return EventSource.GetSources().Single(source => source.Name == "Assimalign.Cohesion.Hosting.Resources");
    }

    private static ResourceCommand CreateCommand(string kind) =>
        new("command-id-secret", kind, "command-owner-secret", "command-key-secret", "command-payload-secret"u8.ToArray());

    private static void WriteCommand(ControlPlaneExchange exchange, ResourceCommand command)
    {
        using var writer = new Utf8JsonWriter(exchange.Request.Body);
        writer.WriteStartObject();
        writer.WriteString("id", command.Id);
        writer.WriteString("kind", command.Kind);
        writer.WriteString("owner", command.Owner);
        writer.WriteString("key", command.Key);
        writer.WriteBase64String("payload", command.Payload.Span);
        writer.WriteEndObject();
        writer.Flush();
        exchange.Request.Body.Position = 0;
    }

    private static object? Payload(EventWrittenEventArgs item, string name) => item.Payload![item.PayloadNames!.IndexOf(name)];

    private static Task InvokeAsync(IResourceControlPlane plane, ControlPlaneExchange exchange) =>
        ResourceControlPlaneMiddleware.InvokeAsync(plane, new ResourceContext(resourceName: "diagnostics-resource"),
            true, null, exchange, static _ => throw new InvalidOperationException("The command route must be terminal."));
}
