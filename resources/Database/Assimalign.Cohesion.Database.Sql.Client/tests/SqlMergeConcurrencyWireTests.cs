using System;
using System.Diagnostics.Tracing;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

[Collection(nameof(SqlClientEventSourceCollection))]
public sealed class SqlMergeConcurrencyWireTests
{
    [Theory(DisplayName = "Cohesion Test [Sql.Client] - MERGE: committed target changes retain coded Snapshot refusal and connection reuse on both transports (#1715)")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ExecuteAsync_ChangedSnapshotTarget_ShouldRefuseWithoutPartialWrites(bool tcp, bool delete)
    {
        // Arrange: wire BEGIN uses the documented default Snapshot isolation.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await SqlClientTestHarness.StartAsync(tcp: tcp);
        await using var holder = await harness.Client.ConnectAsync(timeout.Token);
        await using var contender = await harness.Client.ConnectAsync(timeout.Token);
        await holder.ExecuteAsync("CREATE TABLE t(id INT PRIMARY KEY,v INT)", cancellationToken: timeout.Token);
        await holder.ExecuteAsync("INSERT INTO t VALUES(1,10)", cancellationToken: timeout.Token);
        await holder.ExecuteAsync("BEGIN", cancellationToken: timeout.Token);
        await holder.ExecuteAsync(delete ? "DELETE FROM t" : "UPDATE t SET v=20", cancellationToken: timeout.Token);
        await contender.ExecuteAsync("BEGIN", cancellationToken: timeout.Token);
        using var waits = new MergeWaitListener();
        var pending = contender.QueryAsync("MERGE INTO t USING (SELECT 1 AS id) AS s ON t.id=s.id " +
            "WHEN MATCHED THEN UPDATE SET v=t.v+1 WHEN NOT MATCHED THEN INSERT VALUES(1,99) RETURNING t.v,merge_action()",
            cancellationToken: timeout.Token).AsTask();
        bool committed = false;
        try
        {
            await waits.Waited.Task.WaitAsync(timeout.Token);

            // Act
            await holder.ExecuteAsync("COMMIT", cancellationToken: timeout.Token);
            committed = true;
            var failure = await Should.ThrowAsync<SqlClientException>(async () => await pending);

            // Assert
            failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS04064");
            failure.ConnectionUsable.ShouldBeTrue();
            await contender.ExecuteAsync("ROLLBACK", cancellationToken: timeout.Token);
            var rows = await contender.QueryAsync("SELECT v FROM t", cancellationToken: timeout.Token);
            rows.Count.ShouldBe(delete ? 0 : 1);
            if (!delete) { rows[0][0].ShouldBe(20); }
        }
        finally
        {
            if (!committed) { await holder.ExecuteAsync("ROLLBACK", cancellationToken: CancellationToken.None); }
            await ((Task)pending).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    private sealed class MergeWaitListener : EventListener
    {
        public TaskCompletionSource Waited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == "Assimalign.Cohesion.Database.Transactions")
            { EnableEvents(eventSource, EventLevel.Verbose, (EventKeywords)0x2); }
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.EventName == "LockWaitStart" && eventData.Payload is { Count: > 0 } payload && Equals(payload[0], "app"))
            { Waited.TrySetResult(); }
        }
    }
}
