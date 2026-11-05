using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Sql.Client;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Protocol;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSortSpillConformanceTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Spill sources: grouped and view outputs keep source evaluation and work (#1789)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_GroupOrViewSource_ShouldMatchReference(bool grouped)
    {
        // Arrange
        await using var reference = await Create("ref-source", 67108864);
        await using var engine = await Create("spill-source", 1048576);
        await using var a = await (await reference.CreateDatabaseAsync("db")).CreateSessionAsync();
        await using var b = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await Seed(a); await Seed(b);
        await a.ExecuteAsync("CREATE VIEW v AS SELECT id FROM t"); await b.ExecuteAsync("CREATE VIEW v AS SELECT id FROM t");
        string query = grouped
            ? "SELECT id%1000 AS k,COUNT(*) AS n,wide(id%1000) AS value FROM t GROUP BY id%1000 ORDER BY value,k DESC"
            : "SELECT id,wide(id) AS value FROM v ORDER BY value,id DESC";

        // Act
        await using var left = (DatabaseResultSet)await a.ExecuteAsync(query);
        await using var right = (DatabaseResultSet)await b.ExecuteAsync(query);

        // Assert
        right.ShouldBeOfType<SqlSpilledResultSet>(); (await Rows(right)).ShouldBe(await Rows(left));
        b.LastStatementMetrics!.RecordsExamined.ShouldBe(a.LastStatementMetrics!.RecordsExamined);
        b.LastStatementMetrics.JoinCandidatePairs.ShouldBe(a.LastStatementMetrics.JoinCandidatePairs);
        b.LastStatementMetrics.AccessPath.ShouldBe(a.LastStatementMetrics.AccessPath);
        b.LastStatementMetrics.HashMemoryBytes.ShouldBe(0); b.LastStatementMetrics.PlainSelectMemoryBytes.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Spill wire: InMemory and TCP delivery releases temp space on success, failure and disconnect (#1789)")]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public async Task QueryAsync_WireDelivery_ShouldReleaseTempFiles(bool tcp, int exit)
    {
        // Arrange
        using var ceiling = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = await Create("wire", 262144);
        var database = await engine.CreateDatabaseAsync("db", ceiling.Token);
        await using (var seedSession = await database.CreateSessionAsync(ceiling.Token)) { await Seed(seedSession); }
        await using IDuplexConnectionListener listener = tcp
            ? TcpConnectionListener.Create(options => options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0))
            : new InMemoryConnectionListener();
        await using var server = SqlDatabaseServer.Create(engine, new() { Listener = listener });
        await server.StartAsync(ceiling.Token);
        await using var client = SqlClient.Create(new()
        {
            Settings = new() { Database = "db", EndPoint = listener.EndPoint, MaxPoolSize = 1 },
            ConnectionFactory = tcp ? new TcpConnectionFactory() : ((InMemoryConnectionListener)listener).CreateFactory(),
        });
        await using var connection = await client.ConnectAsync(ceiling.Token);
        var serverSession = (SqlDatabaseServerSession)server.Sessions.Single();
        if (exit == 1) { BeforeRead(engine.TempSpace!, () => throw TempException("CK05004", "injected damaged delivery")); }
        if (exit == 2) { serverSession.ExecuteResultOverride = () => throw new InvalidOperationException("delivery override failed"); }

        // Act / Assert
        if (exit == 0)
        {
            var result = await connection.QueryAsync("SELECT id,k FROM t ORDER BY k", cancellationToken: ceiling.Token);
            result.Count.ShouldBe(4000);
        }
        else if (exit == 1)
        {
            var failure = await Should.ThrowAsync<SqlClientException>(async () =>
                await connection.QueryAsync("SELECT id,k FROM t ORDER BY k", cancellationToken: ceiling.Token));
            failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CK05004");
            failure.Diagnostics[0].Start.ShouldNotBeNull(); failure.Diagnostics[0].End.ShouldNotBeNull();
        }
        else
        {
            await Should.ThrowAsync<Exception>(async () => await connection.QueryAsync("SELECT id,k FROM t ORDER BY k", cancellationToken: ceiling.Token));
        }
        BeforeRead(engine.TempSpace!, null);
        while (TempBytes(engine.TempSpace!) != 0) { ceiling.Token.ThrowIfCancellationRequested(); await Task.Delay(10, ceiling.Token); }
        Files(engine).ShouldBe(0); TempBytes(engine.TempSpace!).ShouldBe(0);
        SqlSortSpillMetrics.For(serverSession.DatabaseSession!.LastStatementMetrics!).ShouldNotBeNull();
        if (exit == 1) { (await connection.QueryAsync("SELECT 1", cancellationToken: ceiling.Token)).Count.ShouldBe(1); }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Spill TCP: a reset during delivery releases temp bytes and files (#1789)")]
    public async Task GetRowsAsync_ClientReset_ShouldReleaseResult()
    {
        // Arrange
        using var ceiling = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = await Create("reset", 262144);
        var database = await engine.CreateDatabaseAsync("db", ceiling.Token);
        await using (var session = await database.CreateSessionAsync(ceiling.Token)) { await Seed(session); }
        await using var listener = TcpConnectionListener.Create(options => options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0));
        await using var server = SqlDatabaseServer.Create(engine, new() { Listener = listener });
        await server.StartAsync(ceiling.Token);
        using var socket = await TcpResetPeer.ConnectReadyAsync(listener.EndPoint, "db", ceiling.Token);
        using var stream = new NetworkStream(socket, ownsSocket: false);
        await using var writer = ProtocolFrameWriter.Create(stream, leaveOpen: true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var resume = new ManualResetEventSlim();
        BeforeRead(engine.TempSpace!, () => { entered.TrySetResult(); resume.Wait(ceiling.Token); });
        try
        {
            // Act
            await writer.WriteFrameAsync(new ProtocolFrame((ProtocolMessageType)SqlProtocolMessageType.Execute,
                ProtocolExecuteMessage.Create("SELECT id,k FROM t ORDER BY k").Encode()), ceiling.Token);
            await writer.FlushAsync(ceiling.Token);
            await entered.Task.WaitAsync(ceiling.Token);
            TempBytes(engine.TempSpace!).ShouldBeGreaterThan(0);
            socket.LingerState = new LingerOption(true, 0); socket.Close(); resume.Set();
            while (TempBytes(engine.TempSpace!) != 0) { await Task.Delay(10, ceiling.Token); }

            // Assert
            TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
        }
        finally { resume.Set(); BeforeRead(engine.TempSpace!, null); }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Spill analysis: fields occur only after actual spilling (#1789)")]
    public async Task ExecuteAsync_Explain_ShouldAddOnlyActualSpillFields()
    {
        // Arrange
        await using var engine = await Create("analysis", 262144);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await Seed(session);

        // Act / Assert
        foreach (var (sql, spilled) in new[]
        {
            ("EXPLAIN SELECT id,k FROM t ORDER BY k", false),
            ("EXPLAIN ANALYZE SELECT id,k FROM t ORDER BY k LIMIT 1", false),
            ("EXPLAIN ANALYZE SELECT id,k FROM t ORDER BY k", true),
        })
        {
            await using var result = (DatabaseResultSet)await session.ExecuteAsync(sql);
            string description = string.Join('\n', await Rows(result));
            description.Contains("spilled_bytes=", StringComparison.Ordinal).ShouldBe(spilled);
            description.Contains("spill_runs=", StringComparison.Ordinal).ShouldBe(spilled);
            description.Contains("spill_merge_passes=", StringComparison.Ordinal).ShouldBe(spilled);
            TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Spill concurrency: readers, early exit and disposal exercise ownership races with counted progress (#1789)")]
    public async Task GetRowsAsync_DisposalRace_ShouldReleaseExactlyOnce()
    {
        // Arrange
        using var ceiling = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = await Create("race", 262144);
        var database = await engine.CreateDatabaseAsync("db", ceiling.Token);
        await using var session = await database.CreateSessionAsync(ceiling.Token);
        await Seed(session);
        int read = 0, disposed = 0, early = 0, rounds = 0;
        var elapsed = Stopwatch.StartNew();

        // Act: a minimum time plus recorded progress, with a ceiling; no duration decides success.
        while (elapsed.Elapsed < TimeSpan.FromMilliseconds(100) || read < 20 || disposed == 0 || early == 0)
        {
            ceiling.Token.ThrowIfCancellationRequested();
            await using var result = (DatabaseResultSet)await session.ExecuteAsync("SELECT id,k FROM t ORDER BY k", cancellationToken: ceiling.Token);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var reader = Task.Run(async () =>
            {
                try
                {
                    await foreach (var row in result.GetRowsAsync(ceiling.Token))
                    {
                        Interlocked.Increment(ref read); started.TrySetResult(); await Task.Yield();
                        if ((rounds & 1) == 0) { Interlocked.Increment(ref early); break; }
                    }
                }
                catch (ObjectDisposedException) { Interlocked.Increment(ref disposed); }
            }, ceiling.Token);
            await started.Task.WaitAsync(ceiling.Token);
            await Task.WhenAll(result.DisposeAsync().AsTask(), reader).WaitAsync(ceiling.Token);
            rounds++;
            TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
        }

        // Assert
        read.ShouldBeGreaterThanOrEqualTo(20); disposed.ShouldBeGreaterThan(0); early.ShouldBeGreaterThan(0);
        _output.WriteLine($"RACE|rounds={rounds}|rows={read}|disposed={disposed}|early={early}");
    }
}
