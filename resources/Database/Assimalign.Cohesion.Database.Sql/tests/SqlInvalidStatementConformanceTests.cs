using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Client;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>The invalid-statement corpus runs through both language execution boundaries.</summary>
public sealed class SqlInvalidStatementConformanceTests
{
    public static IEnumerable<object[]> InvalidStatements()
    {
        yield return ["UPDATE t SET a = 1, a = 2", "COHSQLE019", "a"];
        yield return ["UPDATE t SET a = 1, a = 2 WHERE 1 = 0", "COHSQLE019", "a"];
        yield return ["UPDATE t SET a = DEFAULT, A = 2", "COHSQLE019", "A"];
        yield return ["CREATE TABLE d (a INT, CONSTRAINT pk_d PRIMARY KEY(a, A))", "COHSQLE019", "pk_d"];
        yield return ["CREATE TABLE d (a INT, CONSTRAINT uq_d UNIQUE(a, A))", "COHSQLE019", "uq_d"];
        yield return ["CREATE TABLE d (a INT, CONSTRAINT fk_d FOREIGN KEY(a, A) REFERENCES p(a, b))", "COHSQLE019", "fk_d"];
        yield return ["ALTER TABLE t ADD CONSTRAINT pk_t PRIMARY KEY(a, A)", "COHSQLE019", "pk_t"];
        yield return ["ALTER TABLE t ADD CONSTRAINT uq_t UNIQUE(a, A)", "COHSQLE019", "uq_t"];
        yield return ["ALTER TABLE t ADD CONSTRAINT fk_t FOREIGN KEY(a, A) REFERENCES p(a, b)", "COHSQLE019", "fk_t"];
        yield return ["CREATE TABLE d (a INT PRIMARY KEY, b INT PRIMARY KEY)", "COHSQLE019", "PRIMARY KEY"];
        yield return ["CREATE TABLE d (a INT PRIMARY KEY, b INT, CONSTRAINT pk_d PRIMARY KEY(b))", "COHSQLE019", "pk_d"];
        yield return ["CREATE TABLE d (a INT, b INT, PRIMARY KEY(a), CONSTRAINT pk_d PRIMARY KEY(b))", "COHSQLE019", "pk_d"];
        yield return ["ALTER TABLE p ADD CONSTRAINT pk_p PRIMARY KEY(a)", "COHSQLE019", "pk_p"];
        yield return ["CREATE TABLE d (a VARCHAR(0))", "SQL0005", "a"];
        yield return ["CREATE TABLE d (a DECIMAL(99, 50))", "SQL0005", "a"];
        yield return ["CREATE TABLE d (a DECIMAL(5, 9))", "SQL0005", "a"];
        yield return ["UPDATE t SET a = SUM(a)", "COHSQLE017", "Aggregate functions are not allowed in UPDATE SET."];
        yield return ["UPDATE t SET a = 1 WHERE COUNT(*) > 0", "COHSQLE017", "Aggregate functions are not allowed in WHERE."];
        yield return ["DELETE FROM t WHERE COUNT(*) > 0", "COHSQLE017", "Aggregate functions are not allowed in WHERE."];
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Invalid statements: reject before mutation with coded diagnostics (#1192)")]
    [MemberData(nameof(InvalidStatements))]
    public async Task ExecuteAsync_InvalidStatement_ShouldRejectAndKeepSession(string sql, string code, string detail)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("invalid-statements", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        foreach (string setup in Setup()) { await session.ExecuteAsync(setup, cancellationToken: CancellationToken.None); }

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None));

        // Assert
        failure.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == code);
        failure.Message.ShouldContain(detail, Case.Sensitive);
        if (code == "COHSQLE019" && sql.Contains("(a, A)")) { failure.Message.ShouldContain("A", Case.Sensitive); }
        await using var rows = (await session.ExecuteAsync("SELECT a FROM t", cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        (await SqlT1ScalarTests.ReadAsync(rows)).ShouldHaveSingleItem()[0].ShouldBe(7);
        await session.ExecuteAsync("CREATE TABLE d (a INT, b INT, CONSTRAINT uq_good UNIQUE(a, b))", cancellationToken: CancellationToken.None);
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Invalid statements: the wire preserves diagnostics and connection usability (#1192)")]
    [MemberData(nameof(InvalidStatements))]
    public async Task ExecuteAsync_InvalidStatementOverWire_ShouldRejectAndKeepConnection(string sql, string code, string detail)
    {
        // Arrange
        await using var harness = await ServerTestHarness.StartAsync();
        await using var client = SqlClient.Create(new SqlClientOptions
        {
            Settings = new DatabaseConnectionSettings { Database = ServerTestHarness.DatabaseName, Principal = "invalid-statements", EndPoint = harness.Listener.EndPoint },
            ConnectionFactory = harness.Listener.CreateFactory(),
        });
        await using var connection = await client.ConnectAsync(TestTimeout.Token());
        foreach (string setup in Setup()) { await connection.ExecuteAsync(setup, cancellationToken: TestTimeout.Token()); }

        // Act
        var failure = await Should.ThrowAsync<SqlClientException>(async () =>
            await connection.ExecuteAsync(sql, cancellationToken: TestTimeout.Token()));

        // Assert
        failure.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == code);
        failure.Message.ShouldContain(detail, Case.Sensitive);
        failure.ConnectionUsable.ShouldBeTrue();
        (await connection.QueryAsync("SELECT a FROM t", cancellationToken: TestTimeout.Token())).ShouldHaveSingleItem()["a"].ShouldBe(7);
        await connection.ExecuteAsync("CREATE TABLE d (a INT, b INT, CONSTRAINT uq_good UNIQUE(a, b))", cancellationToken: TestTimeout.Token());
    }

    private static string[] Setup() =>
    [
        "CREATE TABLE t (a INT, b INT)",
        "INSERT INTO t VALUES (7, 8)",
        "CREATE TABLE p (a INT, b INT, PRIMARY KEY(a, b))",
    ];
}
