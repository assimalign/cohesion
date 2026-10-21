using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Client;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>The execution-diagnostic corpus exercises both SQL execution boundaries (#1653).</summary>
public sealed class SqlExecutionDiagnosticConformanceTests
{
    public static IEnumerable<object[]> Cases()
    {
        yield return ["", "SELECT (SELECT id FROM t)", "COHSQLE046"];
        yield return ["", "INSERT INTO t VALUES (3, 'bad')", "COHSQLE022"];
        yield return ["CREATE TABLE target (v DATE)", "INSERT INTO target VALUES ('bad')", "COHSQLE047"];
        yield return ["CREATE TABLE target (v TIME)", "INSERT INTO target VALUES ('bad')", "COHSQLE047"];
        yield return ["CREATE TABLE target (v TIMESTAMP)", "INSERT INTO target VALUES ('bad')", "COHSQLE047"];
        yield return ["CREATE TABLE target (v TIMESTAMPTZ)", "INSERT INTO target VALUES ('bad')", "COHSQLE047"];
        yield return ["CREATE TABLE target (v INTERVAL)", "INSERT INTO target VALUES ('10675199.02:48:05.4775808')", "COHSQLE048"];
        yield return ["CREATE TABLE target (v UUID)", "INSERT INTO target VALUES ('bad')", "COHSQLE022"];
        yield return ["CREATE TABLE target (v BYTEA)", "INSERT INTO target VALUES ('bad')", "COHSQLE055"];
        yield return ["", "SELECT id FROM t ORDER BY CASE WHEN id = 1 THEN 'a' ELSE id END", "COHSQLE055"];
        yield return ["", "CREATE TABLE t (id INT)", "COHSQLE049"];
        yield return ["", "CREATE INDEX uq_t ON t(v)", "COHSQLE050"];
        yield return ["", "ALTER TABLE t ADD CONSTRAINT fk FOREIGN KEY(v) REFERENCES absent(id)", "COHSQLE051"];
        yield return ["", "ALTER TABLE t DROP CONSTRAINT absent", "COHSQLE052"];
        yield return ["", "ALTER TABLE parent DROP COLUMN id", "COHSQLE056"];
        yield return ["CREATE TABLE target (v INT)", "ALTER TABLE target DROP COLUMN v", "COHSQLE053"];
        yield return ["", "ALTER TABLE t ADD CONSTRAINT fk FOREIGN KEY(v) REFERENCES parent(v)", "COHSQLE054"];
        yield return ["CREATE TABLE target (v TEXT)", "ALTER TABLE target ADD CONSTRAINT fk FOREIGN KEY(v) REFERENCES parent(id)", "COHSQLE055"];
        yield return ["CREATE TABLE target (v CHAR(4)); INSERT INTO target VALUES ('x')", "ALTER TABLE target ALTER COLUMN v CHAR(2)", "COHSQLE057"];
        yield return ["", "ALTER TABLE t ADD CONSTRAINT ck CHECK(CURRENT_TIMESTAMP IS NOT NULL)", "COHSQLE059"];
        yield return ["", "ALTER TABLE t ADD COLUMN id INT", "COHSQLE061"];
        yield return ["CREATE DOMAIN d AS INT; CREATE TABLE target (v d)", "DROP DOMAIN d", "COHSQLD002"];
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Conformance: execution and DDL failures carry codes and statement spans (#1653)")]
    [MemberData(nameof(Cases))]
    public async Task ExecuteAsync_ExecutionFailure_ShouldRetainDiagnosticAndTransaction(string extra, string sql, string code)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("execution-diagnostics", new());
        var database = await engine.CreateDatabaseAsync("app", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        foreach (string setup in Setup(extra)) { await session.ExecuteAsync(setup, cancellationToken: timeout.Token); }
        // DDL is self-committing and is refused inside an explicit transaction. DML and SELECT
        // additionally prove that earlier work survives the rejected statement.
        bool dml = sql.StartsWith("INSERT", StringComparison.Ordinal) || sql.StartsWith("SELECT", StringComparison.Ordinal);
        if (dml)
        {
            await session.ExecuteAsync("BEGIN", cancellationToken: timeout.Token);
            await session.ExecuteAsync("INSERT INTO marker VALUES (9)", cancellationToken: timeout.Token);
        }
        var request = SqlQueryRequest.FromSql("\n  " + sql);

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync(request, timeout.Token));

        // Assert
        CheckDiagnostic(failure.Diagnostics, failure.Message, sql, code);
        if (dml) { await session.ExecuteAsync("COMMIT", cancellationToken: timeout.Token); }
        await using var rows = (DatabaseResultSet)await session.ExecuteAsync("SELECT id FROM t ORDER BY id", cancellationToken: timeout.Token);
        (await SqlT1ScalarTests.ReadAsync(rows)).Count.ShouldBe(2);
        await using var markers = (DatabaseResultSet)await session.ExecuteAsync("SELECT id FROM marker", cancellationToken: timeout.Token);
        (await SqlT1ScalarTests.ReadAsync(markers)).Count.ShouldBe(dml ? 1 : 0);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Conformance: execution diagnostics cross the wire and preserve the connection (#1653)")]
    [MemberData(nameof(Cases))]
    public async Task ExecuteAsync_ExecutionFailureOverWire_ShouldRetainDiagnosticAndConnection(string extra, string sql, string code)
    {
        // Arrange
        await using var harness = await ServerTestHarness.StartAsync();
        await using var client = SqlClient.Create(new SqlClientOptions
        {
            Settings = new DatabaseConnectionSettings { Database = ServerTestHarness.DatabaseName, Principal = "diagnostics", EndPoint = harness.Listener.EndPoint },
            ConnectionFactory = harness.Listener.CreateFactory(),
        });
        await using var connection = await client.ConnectAsync(TestTimeout.Token());
        foreach (string setup in Setup(extra)) { await connection.ExecuteAsync(setup, cancellationToken: TestTimeout.Token()); }

        // Act
        var failure = await Should.ThrowAsync<SqlClientException>(async () =>
            await connection.QueryAsync("\n  " + sql, new Dictionary<string, object?> { ["p"] = 0 }, TestTimeout.Token()));

        // Assert
        failure.Kind.ShouldBe(SqlClientErrorKind.ExecutionFailure);
        failure.ConnectionUsable.ShouldBeTrue();
        var detail = failure.Diagnostics.ShouldHaveSingleItem().Message!;
        failure.Message.ShouldBe(code + ": " + detail);
        CheckDiagnostic(failure.Diagnostics, detail, sql, code);
        (await connection.QueryAsync("SELECT id FROM t", cancellationToken: TestTimeout.Token())).Count.ShouldBe(2);
        harness.Server.Sessions.Count.ShouldBe(1);
    }

    private static IEnumerable<string> Setup(string extra)
    {
        yield return "CREATE TABLE t (id INT NOT NULL, v INT)";
        yield return "CREATE UNIQUE INDEX uq_t ON t(id)";
        yield return "INSERT INTO t VALUES (1,1),(2,2)";
        yield return "CREATE TABLE parent (id INT PRIMARY KEY, v INT)";
        yield return "CREATE TABLE marker (id INT)";
        foreach (string statement in extra.Split(';', StringSplitOptions.RemoveEmptyEntries)) { yield return statement; }
    }

    private static void CheckDiagnostic(IReadOnlyList<Diagnostic> diagnostics, string message, string sql, string code)
    {
        var span = SqlQueryRequest.FromSql("\n  " + sql).Statement.SqlExpression.Location!;
        var diagnostic = diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe(code);
        diagnostic.Message.ShouldBe(message);
        diagnostic.Severity.ShouldBe(DiagnosticSeverity.Error);
        diagnostic.Location.ShouldBe(DiagnosticLocation.Absolute);
        diagnostic.Start.ShouldBe(3);
        diagnostic.End.ShouldBe(span.End);
        diagnostic.Line.ShouldBe(span.StartLine);
        message.ShouldNotContain(code + ":", Case.Sensitive);
    }
}
