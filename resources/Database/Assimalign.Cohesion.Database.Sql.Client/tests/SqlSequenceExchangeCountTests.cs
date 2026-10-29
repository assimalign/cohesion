using System;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Testing;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

[Collection(nameof(SqlClientEventSourceCollection))]
public sealed class SqlSequenceExchangeCountTests
{
    private readonly ITestOutputHelper _output;
    public SqlSequenceExchangeCountTests(ITestOutputHelper output) { _output = output; }

    [Theory(DisplayName = "Cohesion Test [Sql.Client] - Pool return: wire exchange counts follow retained session state")]
    [InlineData(false, "idle", 0)][InlineData(true, "idle", 0)]
    [InlineData(false, "select", 1)][InlineData(true, "select", 1)]
    [InlineData(false, "transaction", 2)][InlineData(true, "transaction", 2)]
    [InlineData(false, "nextval", 2)][InlineData(true, "nextval", 2)]
    [InlineData(false, "discarded", 2)][InlineData(true, "discarded", 2)]
    public async Task DisposeAsync_LeaseState_ShouldSendOnlyRequiredExchanges(bool tcp, string scenario, int expected)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await SqlClientTestHarness.StartAsync(tcp: tcp, configureSettings: settings => settings.MaxPoolSize = 1);
        await using (var setup = await harness.Client.ConnectAsync(timeout.Token))
        { await setup.ExecuteAsync("CREATE SEQUENCE s", cancellationToken: timeout.Token); }
        var source = EventSource.GetSources().Single(item => item.Name == "Assimalign.Cohesion.Database.Protocol");
        using var recorder = new EventSourceRecorder(source, EventLevel.Verbose);
        Guid session = harness.Server.Sessions.Single().Id;
        Task<SqlConnection>? waiter = null;

        // Act
        await using (var lease = await harness.Client.ConnectAsync(timeout.Token))
        {
            if (scenario == "select") { await lease.QueryAsync("SELECT 1", cancellationToken: timeout.Token); }
            if (scenario == "transaction") { await lease.ExecuteAsync("BEGIN", cancellationToken: timeout.Token); }
            if (scenario is "nextval" or "discarded") { await lease.QueryAsync("SELECT nextval('s')", cancellationToken: timeout.Token); }
            if (scenario == "discarded") { await lease.ExecuteAsync("DISCARD ALL", cancellationToken: timeout.Token); }
            waiter = harness.Client.ConnectAsync(timeout.Token).AsTask();
            waiter.IsCompleted.ShouldBeFalse();
        }

        // Assert: the stream writer reports each Execute frame once, including pool cleanup.
        int exchanges = recorder.Events.Count(item => item.EventName == "FrameWritten" && item.Payload?[0] is "5");
        _output.WriteLine($"transport={(tcp ? "TCP" : "InMemory")}; lease={scenario}; exchanges={exchanges}");
        exchanges.ShouldBe(expected);
        await using var next = await waiter!;
        harness.Server.Sessions.Single().Id.ShouldBe(session);
    }
}
