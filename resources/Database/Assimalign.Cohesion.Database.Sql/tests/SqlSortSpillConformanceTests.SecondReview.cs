using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Client;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSortSpillConformanceTests
{
    private static DatabaseRow? _wrapperProbe;
    private static object? _cellProbe;

    [Theory(DisplayName = "Cohesion Test [Sql] - Application binary keys preserve fitting aliases and refuse spilling on both engines (#1789)")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ExecuteAsync_ChangingBinaryFunction_ShouldKeepFittingResultsAndRefuseSpill(bool file, bool fits)
    {
        // Arrange
        var builder = SqlDatabaseEngine.CreateBuilder("changing-binary");
        if (file) { builder.Options.RootPath = Path.Combine(_root, "file"); }
        else { builder.Options.TempDirectory = Path.Combine(_root, "memory"); }
        builder.Options.StatementMemoryBudget = fits ? 67108864 : 262144;
        byte[] shared = [0]; var gate = new object(); var calls = new List<long>();
        builder.Functions.Add(SqlScalarFunction.Create("changing_key", (long id) =>
        { lock (gate) { calls.Add(id); shared[0] = (byte)(id % 251); return shared; } }));
        await using var engine = await builder.BuildAsync();
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await Seed(session);
        string[] statements =
        [
            "SELECT id FROM t ORDER BY changing_key(id)",
            "SELECT id FROM t ORDER BY CASE WHEN id>=0 THEN changing_key(id) ELSE X'00' END",
            "SELECT changing_key(id) FROM t ORDER BY id",
            "SELECT CASE WHEN id>=0 THEN changing_key(id) ELSE X'00' END FROM t ORDER BY id",
            "SELECT id FROM t ORDER BY CASE WHEN id>=3000 THEN changing_key(id) ELSE X'00' END",
        ];
        var missingRefusals = new List<string>();
        foreach (string sql in statements)
        {
            calls.Clear();
            // Act / Assert
            if (fits)
            {
                await using var result = (DatabaseResultSet)await session.ExecuteAsync(sql);
                result.ShouldBeOfType<SqlMaterializedResultSet>();
                var rows = await Rows(result);
                if (sql.StartsWith("SELECT id", StringComparison.Ordinal))
                { rows.Select(row => (int)row.Cells[0]!).ShouldBe(Enumerable.Range(0, 4000)); }
                else
                { rows.Select(row => ((byte[])row.Cells[0]!)[0]).ShouldBe(Enumerable.Range(0, 4000).Select(id => (byte)(id % 251))); }
                calls.ShouldBe(Enumerable.Range(sql.Contains("id>=3000", StringComparison.Ordinal) ? 3000 : 0,
                    sql.Contains("id>=3000", StringComparison.Ordinal) ? 1000 : 4000).Select(id => (long)id));
            }
            else
            {
                DatabaseResultSet? returned = null;
                var error = await Record.ExceptionAsync(async () => returned = (DatabaseResultSet)await session.ExecuteAsync(sql));
                if (returned is not null)
                {
                    await using (returned)
                    {
                        var rows = await Rows(returned);
                        _output.WriteLine($"BINARY_BASE|file={file}|sql={sql}|first={string.Join(',', rows.Take(12).Select(row => row.Cells[0] is byte[] bytes ? bytes[0].ToString() : row.Cells[0]!.ToString()))}");
                        missingRefusals.Add(sql);
                    }
                }
                else { error.ShouldBeAssignableTo<DatabaseException>().Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS05067"); }
            }
            TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
        }
        missingRefusals.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Engine binary columns, parameters and built-in results remain spillable (#1789)")]
    public async Task ExecuteAsync_EngineBinaryValues_ShouldMatchFittingSort()
    {
        // Arrange
        await using var reference = await Create("binary-reference", 67108864);
        await using var engine = await Create("binary-spill", 262144);
        await using var a = await (await reference.CreateDatabaseAsync("db")).CreateSessionAsync();
        await using var b = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        foreach (var session in new[] { a, b })
        {
            await Seed(session);
            await session.ExecuteAsync("ALTER TABLE t ADD COLUMN bin BINARY DEFAULT X'00FF'");
        }
        var parameters = new Dictionary<string, object?> { ["binary"] = new byte[] { 1, 2 } };
        foreach (string expression in new[] { "bin", "@binary", "COALESCE(NULL,bin)", "CAST('{\"x\":1}' AS JSONB)" })
        {
            foreach (string key in new[] { "value", expression })
            {
                string sql = $"SELECT id,{expression} AS value FROM t ORDER BY {key},id DESC";
                // Act
                await using var left = (DatabaseResultSet)await a.ExecuteAsync(sql, parameters);
                await using var right = (DatabaseResultSet)await b.ExecuteAsync(sql, parameters);
                // Assert
                right.ShouldBeOfType<SqlSpilledResultSet>(); (await Rows(right)).ShouldBe(await Rows(left));
            }
        }
        const string aggregate = "SELECT id%128 AS key,MAX(bin) AS binary_result,wide(id%128) FROM t GROUP BY id%128 ORDER BY binary_result,key";
        await using var fitting = (DatabaseResultSet)await a.ExecuteAsync(aggregate);
        await using var spilled = (DatabaseResultSet)await b.ExecuteAsync(aggregate);
        spilled.ShouldBeOfType<SqlSpilledResultSet>(); (await Rows(spilled)).ShouldBe(await Rows(fitting));
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Default-grant readers charge decoded 3.9M-character rows before allocating a third workspace (#1789)")]
    public async Task GetRowsAsync_DefaultGrantWideReaders_ShouldStayWithinLiveAllowance()
    {
        // Arrange: a returned helper frame leaves no fixture string rooted at the GC baseline.
        const long grant = 67108864, allowance = 74344038;
        await using var engine = SqlDatabaseEngine.Create("decoded-reader", new() { RootPath = Path.Combine(_root,"decoded-reader"), StatementMemoryBudget = grant });
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await using (var warm = BuildWideReaderResult(session, 3900000)) { }
        long baseline = GC.GetTotalMemory(true);
        await using var result = BuildWideReaderResult(session, 3900000);
        await using var first = result.GetRowsAsync().GetAsyncEnumerator();
        await using var second = result.GetRowsAsync().GetAsyncEnumerator();
        await using var third = result.GetRowsAsync().GetAsyncEnumerator();
        (await first.MoveNextAsync()).ShouldBeTrue(); (await second.MoveNextAsync()).ShouldBeTrue();
        long admitted = ReaderBytes(result);
        // Act
        var failure = await Record.ExceptionAsync(async () => await third.MoveNextAsync());
        long live = GC.GetTotalMemory(true) - baseline;
        // Assert
        _output.WriteLine($"DECODED_READER_PROBE|workspace={3 * Sorted(result)!.BufferBytes}|live={live}|allowance={allowance}|third_refused={failure is not null}");
        failure.ShouldNotBeNull().ShouldBeAssignableTo<DatabaseException>().Diagnostics[0].Code.ShouldBe("CS05067"); ReaderBytes(result).ShouldBe(admitted);
        live.ShouldBeLessThanOrEqualTo(allowance);
        (await first.MoveNextAsync()).ShouldBeTrue(); (await second.MoveNextAsync()).ShouldBeTrue();
        first.Current.GetString(1)!.Length.ShouldBe(3900000); second.Current.GetString(1)!.Length.ShouldBe(3900000);
        _output.WriteLine($"DECODED_READERS|workspace={2 * Sorted(result)!.BufferBytes}|charged={admitted}|live={live}|allowance={allowance}|readers=2");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static SqlSpilledResultSet BuildWideReaderResult(SqlDatabaseSession owner, int characters)
    {
        var metrics = new SqlStatementMetrics();
        using var collector = new SqlRowCollector([new(SqlBoundConstant.Null, false, true, new SqlBoundCollation(Collation.Binary))],
            null, null, null, CancellationToken.None, metrics, 67108864);
        for (int id = 0; id < 50; id++) { collector.Add([id, new string('a', characters)], [id], owner); }
        var result = collector.TakeSpilled([], 0, -1); result.CaptureMetrics(metrics); return result;
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Decoded row expansion refuses an undeliverable first reader before publishing the result (#1789)")]
    public async Task ExecuteAsync_MaximumDecodedRow_ShouldRefuseBeforeDelivery()
    {
        // Arrange: legal encoded length, but decoding creates a box for every aliased Boolean cell.
        MeasureRowWrapperBytes().ShouldBe(32);
        MeasureStringBytes(1).ShouldBe(24);
        MeasureStringBytes(2).ShouldBe(32);
        const int grant = 67108864, bound = (grant - 32768) / 16 - 8192;
        object?[] row = Enumerable.Repeat<object?>(true, (bound - 5 - 16 - 1) / 2).ToArray();
        SqlTempRowCodec.TryGetLength(row, out int length).ShouldBeTrue();
        long decoded = 56L + 32L * row.Length;
        await using var engine = await Create("decoded-first", grant);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        var metrics = new SqlStatementMetrics();
        using var collector = new SqlRowCollector([new(SqlBoundConstant.Null, false, true, new SqlBoundCollation(Collation.Binary))],
            null, null, null, CancellationToken.None, metrics, grant);
        collector.Add(row, [null], session);
        // Act / Assert
        var error = Should.Throw<DatabaseException>(() => collector.TakeSpilled([], 0, -1));
        error.Diagnostics[0].Code.ShouldBe("CS05067");
        TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
        _output.WriteLine($"DECODED_LARGEST|workspace={8L * (8192 + bound)}|retained_peak={8L * (8192 + bound) + decoded}|overlap_peak={8L * (8192 + bound) + 2 * decoded}|grant={grant}");
        _output.WriteLine($"DECODED_FIRST|encoded={length}|decoded={decoded}|one_reader={8192L + length + 17 + decoded}|overlap_peak={8192L + length + 17 + 2 * decoded}|grant={grant}");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureRowWrapperBytes()
    {
        // Escape to a static root so the JIT cannot stack-allocate the measured object.
        var values = Array.Empty<object?>();
        _wrapperProbe = new SqlMaterializedRow(values);
        long before = GC.GetAllocatedBytesForCurrentThread();
        _wrapperProbe = new SqlMaterializedRow(values);
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        GC.KeepAlive(_wrapperProbe); _wrapperProbe = null;
        return bytes;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureStringBytes(int characters)
    {
        _cellProbe = new string('a', characters);
        long before = GC.GetAllocatedBytesForCurrentThread();
        _cellProbe = new string('a', characters);
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        GC.KeepAlive(_cellProbe); _cellProbe = null;
        return bytes;
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - TCP cancellation aborts backpressured delivery and releases spill before lease disposal (#1789)")]
    public async Task QueryAsync_TcpCancellation_ShouldReleaseSpillWithUndisposedConnection()
    {
        // Arrange
        using var ceiling = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var engine = await Create("tcp-cancel", 262144);
        var database = await engine.CreateDatabaseAsync("db");
        await using (var seedSession = await database.CreateSessionAsync()) { await Seed(seedSession); }
        await using var listener = TcpConnectionListener.Create(options => options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0));
        await using var server = SqlDatabaseServer.Create(engine, new() { Listener = listener });
        await server.StartAsync(ceiling.Token);
        var factory = new PausedTcpConnectionFactory();
        await using var client = SqlClient.Create(new() { ConnectionFactory = factory,
            Settings = new() { Database = "db", EndPoint = listener.EndPoint, MaxPoolSize = 1 } });
        await using var connection = await client.ConnectAsync(ceiling.Token);
        var transport = factory.Latest!;
        var socket = TcpSocket(listener.Connections.Single());
        // Fix the socket windows so backpressure does not depend on OS autotuning.
        TcpSocket(transport.Inner).ReceiveBufferSize = 16384; socket.SendBufferSize = 16384;
        transport.PauseReads = true;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ceiling.Token);
        Task query = connection.QueryAsync("SELECT stamp(id) AS marker,wide(id) AS value FROM t ORDER BY marker", cancellationToken: cancellation.Token).AsTask();
        while (TempBytes(engine.TempSpace!) == 0 || socket.Poll(0, SelectMode.SelectWrite))
        { await Task.Delay(10, ceiling.Token); }
        long retained = TempBytes(engine.TempSpace!); int files = Files(engine);
        // Act
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(async () => await query.WaitAsync(ceiling.Token));
        _output.WriteLine($"TCP_CANCEL_PROBE|temp={TempBytes(engine.TempSpace!)}|files={Files(engine)}|aborts={transport.Aborts}|client_open={connection.IsOpen}");
        transport.Aborts.ShouldBe(1);
        while (TempBytes(engine.TempSpace!) != 0 || Files(engine) != 0)
        { await Task.Delay(10, ceiling.Token); }
        // Assert: the connection object is still undisposed and unusable; pool return discards it.
        connection.IsOpen.ShouldBeFalse(); transport.Aborts.ShouldBe(1);
        await Should.ThrowAsync<SqlClientException>(async () => await connection.QueryAsync("SELECT 1", cancellationToken: ceiling.Token));
        _output.WriteLine($"TCP_CANCEL|before_bytes={retained}|before_files={files}|write_ready=false|after_bytes={TempBytes(engine.TempSpace!)}|after_files={Files(engine)}|undisposed=true");
        await connection.DisposeAsync();
        await using var replacement = await client.ConnectAsync(ceiling.Token);
        factory.Connects.ShouldBe(2);
        (await replacement.QueryAsync("SELECT 1", cancellationToken: ceiling.Token)).Count.ShouldBe(1);
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_socket")]
    private static extern ref Socket TcpSocket([UnsafeAccessorType("Assimalign.Cohesion.Connections.Tcp.Internal.TcpConnection, Assimalign.Cohesion.Connections.Tcp")] object connection);
}
