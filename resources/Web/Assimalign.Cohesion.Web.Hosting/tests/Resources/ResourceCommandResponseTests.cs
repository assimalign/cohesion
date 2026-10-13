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
public sealed class ResourceCommandResponseTests
{
    [Theory(DisplayName = "Cohesion Test [Web.Hosting] - Command response: write failures preserve the applied outcome")]
    [InlineData("POST", "Apply")]
    [InlineData("DELETE", "Delete")]
    public async Task InvokeAsync_ResponseWriteArgumentException_ShouldPreserveAppliedOutcomeAndPropagateFailure(string method, string operation)
    {
        // Arrange
        string kind = "registered." + Guid.NewGuid().ToString("N");
        IResourceControlPlane plane = ResourceControlPlane.Create([kind]);
        plane.RegisterCommandHandler(new ScriptedCommandHandler(kind));
        var command = new ResourceCommand("command-id", kind, "command-owner", "command-key", ReadOnlyMemory<byte>.Empty);
        await plane.ExecuteCommandAsync(method == "DELETE" ? command : command with { Id = "warmup-id", Key = "warmup-key" }, CancellationToken.None);
        EventSource producer = EventSource.GetSources().Single(source => source.Name == "Assimalign.Cohesion.Hosting.Resources");
        await Task.Delay(TimeSpan.FromMilliseconds(1100), CancellationToken.None);
        using var producerRecorder = new EventSourceRecorder(producer, EventLevel.Verbose);
        using var webRecorder = new EventSourceRecorder(WebServerEventSource.Log, EventLevel.Verbose);
        await using var exchange = new ControlPlaneExchange("/cohesion/v1/commands", new HttpMethod(method));
        var requestId = new ControlPlaneRequestIdFeature();
        exchange.Features.Set<IWebRequestIdFeature>(requestId);
        var responseFailure = new ArgumentException("response-adapter-secret");
        await exchange.Response.Body.DisposeAsync();
        exchange.Response.Body = new FailingCommandResponseStream(responseFailure);
        using (var writer = new Utf8JsonWriter(exchange.Request.Body))
        {
            writer.WriteStartObject();
            writer.WriteString("id", command.Id);
            writer.WriteString("kind", command.Kind);
            writer.WriteString("owner", command.Owner);
            writer.WriteString("key", command.Key);
            writer.WriteEndObject();
            writer.Flush();
        }
        exchange.Request.Body.Position = 0;

        // Act
        ArgumentException failure = await Should.ThrowAsync<ArgumentException>(() =>
            ResourceControlPlaneMiddleware.InvokeAsync(plane, new ResourceContext(resourceName: "response-resource"),
                true, null, exchange, static _ => throw new InvalidOperationException("The command route must be terminal.")));

        // Assert
        failure.ShouldBeSameAs(responseFailure);
        EventWrittenEventArgs applied = producerRecorder.Events.Where(item => item.EventId == 2 &&
            Equals(Payload(item, "kind"), kind)).ShouldHaveSingleItem();
        applied.EventName.ShouldBe("CommandApplied");
        Payload(applied, "operation").ShouldBe(operation);
        producerRecorder.Events.Where(item => item.EventId is >= 2 and <= 5).ShouldHaveSingleItem().ShouldBeSameAs(applied);
        webRecorder.Events.ShouldNotContain(item => item.EventId == 17 &&
            Equals(Payload(item, "requestId"), requestId.ExpectedId));
        exchange.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        exchange.Response.Headers[HttpHeaderKey.ContentType].Value.ShouldBe("application/octet-stream");
        producerRecorder.Events.ShouldNotContain(item => item.EventId == 0);
        webRecorder.Events.ShouldNotContain(item => item.EventId == 0);
    }

    private static object? Payload(EventWrittenEventArgs item, string name) => item.Payload![item.PayloadNames!.IndexOf(name)];
}
