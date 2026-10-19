using System;
using System.Linq;
using System.Net;
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
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlDiagnosticOverflowTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Sql] - diagnostic overflow preserves parse failures, success rows and session reuse")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_DiagnosticOverflow_ShouldKeepOriginalFailureResultsAndSession(bool tcp)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("diagnostic-overflow", new());
        var database = await engine.CreateDatabaseAsync("app", TestTimeout.Token());
        await using IDuplexConnectionListener listener = tcp
            ? TcpConnectionListener.Create(options => options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0))
            : new InMemoryConnectionListener();
        await using var server = SqlDatabaseServer.Create(engine, new() { Listener = listener, ShutdownDrainTimeout = TimeSpan.FromSeconds(2) });
        await server.StartAsync(TestTimeout.Token());
        IDuplexConnectionFactory factory = tcp ? new TcpConnectionFactory() : ((InMemoryConnectionListener)listener).CreateFactory();
        await using var client = SqlClient.Create(new()
        {
            Settings = new() { Database = "app", EndPoint = listener.EndPoint, MaxPoolSize = 1 },
            ConnectionFactory = factory,
        });
        await using var connection = await client.ConnectAsync(TestTimeout.Token());
        var session = (SqlDatabaseServerSession)server.Sessions.Single();
        string sql = "SELECT " + string.Join(",", Enumerable.Repeat("CURRENT_DATE()", 4097)) + ";";
        var expected = new SqlQueryParser().Parse(sql).Diagnostics.Where(item => item.Severity == DiagnosticSeverity.Error).ToArray();
        // Act
        var parse = await Should.ThrowAsync<SqlClientException>(async () => await connection.QueryAsync(sql, cancellationToken: TestTimeout.Token()));
        // Assert: the original statement error is unchanged; the notice identifies the retained prefix.
        expected.Length.ShouldBe(4097);
        parse.Code.ShouldBe(ProtocolErrorCode.ParseFailure);
        parse.Message.ShouldBe($"SQL parse error {expected[0].Code}: {expected[0].Message}");
        parse.Diagnostics.Count.ShouldBe(4096);
        parse.Diagnostics.Take(4095).Select(Value).ShouldBe(expected.Take(4095).Select(Value));
        parse.Diagnostics[^1].Code.ShouldBe("COHDBP001");
        parse.Diagnostics[^1].Message!.ShouldContain("first 4095 of 4097", Case.Sensitive);
        connection.IsOpen.ShouldBeTrue();

        // Arrange: an oversized warning sits after a small warning and before another small one.
        // Keeping a prefix means the small trailing entry must also be reported as omitted.
        await using var execution = await database.CreateSessionAsync(TestTimeout.Token());
        await execution.ExecuteAsync("CREATE TABLE numbers (id INT);", cancellationToken: TestTimeout.Token());
        await execution.ExecuteAsync("INSERT INTO numbers VALUES (7);", cancellationToken: TestTimeout.Token());
        var rows = (DatabaseResultSet)await execution.ExecuteAsync("SELECT id FROM numbers;", cancellationToken: TestTimeout.Token());
        Diagnostic[] warnings =
        [
            new() { Code = "SQLTEST001", Message = "survives", Severity = DiagnosticSeverity.Warning },
            new() { Code = "SQLTEST002", Message = new string('x', ProtocolDiagnosticsMessage.MaxPayloadLength), Severity = DiagnosticSeverity.Warning },
            new() { Code = "SQLTEST003", Message = "omitted", Severity = DiagnosticSeverity.Warning },
        ];
        session.ExecuteResultOverride = () => new DiagnosticOverflowResult(rows, warnings);
        // Act
        var success = await connection.QueryAsync("SELECT 7;", cancellationToken: TestTimeout.Token());
        // Assert
        success.Count.ShouldBe(1);
        success[0].GetInt32(0).ShouldBe(7);
        success.Diagnostics.Count.ShouldBe(2);
        success.Diagnostics[0].Code.ShouldBe("SQLTEST001");
        success.Diagnostics[^1].Code.ShouldBe("COHDBP001");
        success.Diagnostics[^1].Message!.ShouldContain("first 1 of 3", Case.Sensitive);
        new ProtocolDiagnosticsMessage(success.Diagnostics).Encode().Length.ShouldBeLessThanOrEqualTo(ProtocolDiagnosticsMessage.MaxPayloadLength);

        // Act: the review's single 1 MiB warning leaves only the bounded notice.
        var singleRows = (DatabaseResultSet)await execution.ExecuteAsync("SELECT id FROM numbers;", cancellationToken: TestTimeout.Token());
        session.ExecuteResultOverride = () => new DiagnosticOverflowResult(singleRows, [warnings[1]]);
        var single = await connection.QueryAsync("SELECT 7;", cancellationToken: TestTimeout.Token());
        // Assert
        single.Count.ShouldBe(1);
        single.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDBP001");
        single.Diagnostics[0].Message!.ShouldContain("first 0 of 1", Case.Sensitive);

        // Act: oversized failure diagnostics must keep the original error code and message, too.
        session.ExecuteResultOverride = () => throw new DatabaseException("Original failure", warnings);
        var failure = await Should.ThrowAsync<SqlClientException>(async () => await connection.QueryAsync("SELECT 7;", cancellationToken: TestTimeout.Token()));
        // Assert
        failure.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        failure.Message.ShouldBe("Original failure");
        failure.Diagnostics[^1].Code.ShouldBe("COHDBP001");
        connection.IsOpen.ShouldBeTrue();
        session.ExecuteResultOverride = null;
        (await connection.QueryAsync("SELECT id FROM numbers;", cancellationToken: TestTimeout.Token())).Count.ShouldBe(1);
        server.Sessions.ShouldHaveSingleItem().ShouldBeSameAs(session);
    }

    private static object Value(Diagnostic item) => (item.Code, item.Message, item.Severity, item.Start, item.End, item.Line);
}
