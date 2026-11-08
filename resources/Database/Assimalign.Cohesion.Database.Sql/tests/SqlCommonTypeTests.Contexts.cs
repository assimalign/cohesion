using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Sql.Protocol;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlCommonTypeTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - CASE/COALESCE: function overloads see the common argument type (#1788)")]
    public async Task Execute_FunctionArgument_ShouldSelectCommonTypeOverload()
    {
        // Arrange: PostgreSQL 17 selects the numeric overload for both expressions.
        var builder = SqlDatabaseEngine.CreateBuilder("common-overloads");
        builder.Functions.Add(SqlScalarFunction.Create("kind", static (int value) => "integer", SqlFunctionVolatility.Immutable));
        builder.Functions.Add(SqlScalarFunction.Create("kind", static (decimal value) => "numeric", SqlFunctionVolatility.Immutable));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        // Act
        var rows = await SqlPredicateExecutionTests.RowsAsync(session,
            "SELECT kind(CASE WHEN FALSE THEN 1 ELSE 2.5 END), kind(COALESCE(NULL::INT, 2.5))");

        // Assert
        rows.ShouldBe(new object?[][] { ["numeric", "numeric"] });
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - CASE/COALESCE: incompatible alternatives refuse before empty inputs and skipped branches (#1788)")]
    [InlineData("SELECT COALESCE(n, s) FROM t", "CS02080")]
    [InlineData("SELECT CASE WHEN TRUE THEN n ELSE s END FROM t", "CS02080")]
    [InlineData("SELECT COALESCE(n, 'x') FROM t", "CS02022")]
    [InlineData("SELECT n FROM t WHERE COALESCE(n, 'x') > 0", "CS02022")]
    [InlineData("SELECT n FROM t ORDER BY COALESCE(n, s)", "CS02080")]
    [InlineData("SELECT COALESCE(n, s), COUNT(*) FROM t GROUP BY COALESCE(n, s)", "CS02080")]
    [InlineData("SELECT CASE WHEN FALSE THEN COALESCE(1, 'x') ELSE 0 END", "CS02022")]
    [InlineData("SELECT COALESCE(1, ('x' COLLATE binary))", "CS02022")]
    [InlineData("SELECT COALESCE(1::SMALLINT, '32768')", "CS02002")]
    public async Task Plan_InvalidAlternatives_ShouldRefuseBeforeRows(string sql, string code)
    {
        // Arrange
        await using var engine = SqlPredicateExecutionTests.CreateEngine();
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await Execute(session, "CREATE TABLE t(n INT, s TEXT)");

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(() => SqlPredicateExecutionTests.RowsAsync(session, sql));

        // Assert: the empty relation cannot defer any of these binding errors to a row.
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - CASE/COALESCE: per-node conversions compose in WHERE, GROUP BY and ORDER BY (#1788)")]
    public async Task Execute_NestedContexts_ShouldMatchPostgres17()
    {
        // Arrange
        await using var engine = SqlPredicateExecutionTests.CreateEngine();
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await Execute(session, "CREATE TABLE t(id INT, c BOOLEAN)");
        await Execute(session, "INSERT INTO t VALUES(1, TRUE),(2,FALSE)");

        // Act / Assert: PostgreSQL 17.11 returned (1,1), (2,2.5), one group for 16777216.
        (await SqlPredicateExecutionTests.RowsAsync(session, "SELECT id, COALESCE(CASE WHEN c THEN 1 ELSE 2.5 END, 0) FROM t WHERE CASE WHEN c THEN 1 ELSE 2.5 END > 0 ORDER BY CASE WHEN c THEN 1 ELSE 2.5 END"))
            .ShouldBe(new object?[][] { [1, 1m], [2, 2.5m] });
        (await SqlPredicateExecutionTests.RowsAsync(session, "SELECT COUNT(*) FROM t GROUP BY CASE WHEN c THEN 16777217 ELSE 16777216::REAL END"))
            .ShouldBe(new object?[][] { [2L] });
        (await SqlPredicateExecutionTests.RowsAsync(session, "SELECT COALESCE(CASE WHEN c THEN 16777217 ELSE 16777216::REAL END, 0::DOUBLE PRECISION) FROM t ORDER BY id"))
            .ShouldBe(new object?[][] { [16777216d], [16777216d] });
        (await SqlPredicateExecutionTests.RowsAsync(session, "SELECT COALESCE(CASE WHEN c THEN 16777217 ELSE 16777216::REAL END, 0::DOUBLE PRECISION), COUNT(*) FROM t GROUP BY id,c ORDER BY id"))
            .ShouldBe(new object?[][] { [16777217d, 1L], [16777216d, 1L] });
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - CASE/COALESCE: InMemory and TCP headers and cells carry resolved types (#1788)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_WireMetadata_ShouldMatchResolvedValues(bool tcp)
    {
        // Arrange
        using var ceiling = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlPredicateExecutionTests.CreateEngine();
        await engine.CreateDatabaseAsync("db", ceiling.Token);
        await using IDuplexConnectionListener listener = tcp
            ? TcpConnectionListener.Create(options => options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0))
            : new InMemoryConnectionListener();
        await using var server = SqlDatabaseServer.Create(engine, new() { Listener = listener });
        await server.StartAsync(ceiling.Token);
        IDuplexConnectionFactory factory = tcp ? new TcpConnectionFactory() : ((InMemoryConnectionListener)listener).CreateFactory();
        await using var connection = await factory.ConnectAsync(listener.EndPoint, ceiling.Token);
        var stream = connection.AsStream();
        await using var reader = ProtocolFrameReader.Create(stream, leaveOpen: true);
        await using var writer = ProtocolFrameWriter.Create(stream, leaveOpen: true);
        await Send(ProtocolMessageType.Startup, new ProtocolStartupMessage(ProtocolVersion.Current, "db", "common-types").Encode());
        await Expect(ProtocolMessageType.Authenticate);
        await Send(ProtocolMessageType.AuthenticateResponse);
        await Expect(ProtocolMessageType.Ready);

        // Act
        await Send(ProtocolMessageType.Execute, ProtocolExecuteMessage.Create(
            "SELECT CASE WHEN FALSE THEN 1 ELSE 2.5 END, COALESCE(NULL::INT, '3'), COALESCE(NULL,NULL), COALESCE(NULL::NUMERIC, 1::REAL)").Encode());
        var frame = await Expect(ProtocolMessageType.ResultHeader);
        var header = ProtocolResultHeaderMessage.Decode(frame.Payload.Span);

        // Assert
        header.Columns.Select(column => (DatabaseType)column.Item2).ShouldBe(new[] { DatabaseType.Decimal, DatabaseType.Int32, DatabaseType.String, DatabaseType.Float32 });
        var values = DecodeRow((await Expect(ProtocolMessageType.ResultRow)).Payload);
        values.ShouldBe(new object?[] { 2.5m, 3, null, 1f });
        await Expect(ProtocolMessageType.ResultComplete);
        await Send(ProtocolMessageType.Execute, ProtocolExecuteMessage.Create("SELECT COALESCE(1,'x')").Encode());
        var error = ProtocolErrorMessage.Decode((await Expect(ProtocolMessageType.Error)).Payload.Span);
        error.Message.ShouldContain("CS02022", Case.Sensitive);

        async Task Send(ProtocolMessageType type, ReadOnlyMemory<byte> payload = default)
        {
            await writer.WriteFrameAsync(new ProtocolFrame(type, payload), ceiling.Token);
            await writer.FlushAsync(ceiling.Token);
        }
        async Task<ProtocolFrame> Expect(ProtocolMessageType type)
        {
            var received = await reader.ReadFrameAsync(ceiling.Token);
            received.ShouldNotBeNull();
            received.Value.Type.ShouldBe(type);
            return received.Value;
        }
    }

    private static object?[] DecodeRow(ReadOnlyMemory<byte> payload)
    {
        var values = new List<object?>();
        var reader = new DatabaseKeyReader(payload.Span);
        while (!reader.IsAtEnd) { values.Add(DatabaseValueCodec.Read(ref reader)); }
        return values.ToArray();
    }
}
