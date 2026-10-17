using System;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Documents.Client.Internal;
using Assimalign.Cohesion.Testing;

namespace Assimalign.Cohesion.Database.Documents.Client.Tests;

[CollectionDefinition(nameof(DocumentClientEventSourceCollection), DisableParallelization = true)]
public sealed class DocumentClientEventSourceCollection { }

[Collection(nameof(DocumentClientEventSourceCollection))]
public sealed class DocumentClientEventSourceTests
{
    [Fact(DisplayName = "Cohesion Test [Documents.Client] - EventSource has a strict manifest and assembly name")]
    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode", Justification = "Test-only manifest validation; tests are not trimmed.")]
    [UnconditionalSuppressMessage("Trimming", "IL2111:DynamicallyAccessedMembers", Justification = "Test-only manifest validation; tests are not trimmed.")]
    public void GenerateManifest_StrictMode_ShouldSucceed()
    {
        // Act
        string? manifest = EventSource.GenerateManifest(typeof(DocumentClientEventSource), null, EventManifestOptions.Strict);

        // Assert
        EventSource.GetName(typeof(DocumentClientEventSource)).ShouldBe("Assimalign.Cohesion.Database.Documents.Client");
        manifest.ShouldNotBeNull().ShouldContain("QueryStop");
    }

    [Fact(DisplayName = "Cohesion Test [Documents.Client] - Wire statements trace once and server sessions balance their counters")]
    public async Task QueryAsync_ServerAndClient_ShouldReportOneStatementAndBalancedSession()
    {
        // Arrange
        await using var harness = await DocumentClientTestHarness.StartAsync();
        var source = EventSource.GetSources().Single(s => s.Name == "Assimalign.Cohesion.Database.Documents");
        using var serverEvents = new EventSourceRecorder(source, EventLevel.Verbose, TimeSpan.FromSeconds(0.1));
        using var clientEvents = new EventSourceRecorder(DocumentClientEventSource.Log, EventLevel.Verbose);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.CreateCollectionAsync("items", harness.Token);
        var root = EventSource.GetSources().Single(s => s.Name == "Assimalign.Cohesion.Database");
        using var statements = new EventSourceRecorder(root, EventLevel.Verbose);

        // Act
        await connection.QueryAsync("SELECT * FROM items", cancellationToken: harness.Token);
        await connection.AbortAsync();
        await harness.Server.StopAsync(harness.Token);
        await serverEvents.WaitForCountersAsync(["current-server-sessions", "total-server-sessions", "total-rejected-sessions"], harness.Token);
        var zero = await serverEvents.WaitForCounterAsync("current-server-sessions", serverEvents.CounterSequence, harness.Token);

        // Assert
        statements.Events.Count(e => e.EventName == "StatementStart").ShouldBe(1);
        statements.Events.Count(e => e.EventName == "StatementStop").ShouldBe(1);
        clientEvents.Events.Where(e => e.EventName is "QueryStart" or "QueryStop").Select(e => e.EventName)
            .ShouldBe(["QueryStart", "QueryStop", "QueryStart", "QueryStop"]);
        serverEvents.Events.Count(e => e.EventName == "SessionAccepted").ShouldBe(1);
        serverEvents.Events.Count(e => e.EventName == "SessionClosed").ShouldBe(1);
        zero.Value.ShouldBe(0);
        serverEvents.Events.Concat(clientEvents.Events).Concat(statements.Events).ShouldNotContain(e => e.EventId == 0);
        clientEvents.Events.SelectMany(e => e.Payload ?? []).OfType<string>().Any(text => text.Contains("SELECT", StringComparison.Ordinal)).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Documents.Client] - Repeatable errors throttle and report suppressed failures")]
    public async Task QueryAsync_RepeatedFailures_ShouldThrottleErrorsAndKeepStartStopPairs()
    {
        // Arrange
        await using var harness = await DocumentClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await Task.Delay(1100, harness.Token);
        using var events = new EventSourceRecorder(DocumentClientEventSource.Log, EventLevel.Verbose);
        using var serverEvents = new EventSourceRecorder(EventSource.GetSources().Single(s => s.Name == "Assimalign.Cohesion.Database.Documents"), EventLevel.Verbose);

        // Act
        for (int index = 0; index < 10; index++)
        {
            await Should.ThrowAsync<DocumentClientException>(async () => await connection.QueryAsync("BEGIN", cancellationToken: harness.Token));
        }
        await Task.Delay(1100, harness.Token);
        await Should.ThrowAsync<DocumentClientException>(async () => await connection.QueryAsync("BEGIN", cancellationToken: harness.Token));

        // Assert
        events.Events.Count(e => e.EventName == "QueryStart").ShouldBe(11);
        events.Events.Count(e => e.EventName == "QueryStop").ShouldBe(11);
        var failures = events.Events.Where(e => e.EventName == "QueryFailed").ToArray();
        failures.Length.ShouldBe(2);
        failures[1].PayloadNames!.Last().ShouldBe("suppressedCount");
        failures[1].Payload!.Last().ShouldBe(9);
        var parseFailures = serverEvents.Events.Where(e => e.EventName == "StatementParseFailed").ToArray();
        parseFailures.Length.ShouldBe(2);
        parseFailures[1].Payload!.Last().ShouldBe(9);
        events.Events.Concat(serverEvents.Events).ShouldNotContain(e => e.EventId == 0);
    }
}
