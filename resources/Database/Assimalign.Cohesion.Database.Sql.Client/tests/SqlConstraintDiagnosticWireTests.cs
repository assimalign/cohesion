using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

/// <summary>Verifies constraint diagnostics and statement atomicity over InMemory and TCP transports.</summary>
public sealed class SqlConstraintDiagnosticWireTests
{
    /// <summary>A rejected statement retains its complete diagnostic and leaves its transaction usable.</summary>
    /// <param name="tcp">Whether to use loopback TCP instead of InMemory.</param>
    /// <param name="sql">The statement that violates a constraint.</param>
    /// <param name="code">The required engine diagnostic code.</param>
    /// <param name="constraint">The constraint or domain name.</param>
    /// <param name="table">The constrained table, or empty for a domain CAST.</param>
    /// <returns>The asynchronous verification.</returns>
    [Theory(DisplayName = "Cohesion Test [Sql.Client] - Constraints: exact diagnostics cross both transports and leave the transaction usable (#1666)")]
    [InlineData(false, "INSERT INTO t VALUES (3,10,3,3,1)", "CS03037", "uq_t", "dbo.t")]
    [InlineData(true, "INSERT INTO t VALUES (3,10,3,3,1)", "CS03037", "uq_t", "dbo.t")]
    [InlineData(false, "INSERT INTO t VALUES (1,30,3,3,1)", "CS03037", "pk_t", "dbo.t")]
    [InlineData(true, "INSERT INTO t VALUES (1,30,3,3,1)", "CS03037", "pk_t", "dbo.t")]
    [InlineData(false, "INSERT INTO t VALUES (3,30,3,1,1)", "CS03037", "uq_d", "dbo.t")]
    [InlineData(true, "INSERT INTO t VALUES (3,30,3,1,1)", "CS03037", "uq_d", "dbo.t")]
    [InlineData(false, "INSERT INTO t VALUES (3,30,3,3,1),(4,10,4,4,1)", "CS03037", "uq_t", "dbo.t")]
    [InlineData(true, "INSERT INTO t VALUES (3,30,3,3,1),(4,10,4,4,1)", "CS03037", "uq_t", "dbo.t")]
    [InlineData(false, "INSERT INTO t SELECT * FROM source", "CS03037", "uq_t", "dbo.t")]
    [InlineData(true, "INSERT INTO t SELECT * FROM source", "CS03037", "uq_t", "dbo.t")]
    [InlineData(false, "UPDATE t SET u = 10 WHERE id = 2", "CS03037", "uq_t", "dbo.t")]
    [InlineData(true, "UPDATE t SET u = 10 WHERE id = 2", "CS03037", "uq_t", "dbo.t")]
    [InlineData(false, "INSERT INTO t VALUES (3,30,3,3,1),(1,40,4,4,1) ON CONFLICT(id) DO UPDATE SET u = 20", "CS03037", "uq_t", "dbo.t")]
    [InlineData(true, "INSERT INTO t VALUES (3,30,3,3,1),(1,40,4,4,1) ON CONFLICT(id) DO UPDATE SET u = 20", "CS03037", "uq_t", "dbo.t")]
    [InlineData(false, "INSERT INTO t VALUES (3,30,0,3,1)", "CS03038", "ck_t", "dbo.t")]
    [InlineData(true, "INSERT INTO t VALUES (3,30,0,3,1)", "CS03038", "ck_t", "dbo.t")]
    [InlineData(false, "UPDATE t SET qty = 0 WHERE id = 1", "CS03038", "ck_t", "dbo.t")]
    [InlineData(true, "UPDATE t SET qty = 0 WHERE id = 1", "CS03038", "ck_t", "dbo.t")]
    [InlineData(false, "INSERT INTO t VALUES (3,30,3,0,1)", "CS03038", "positive", "dbo.t")]
    [InlineData(true, "INSERT INTO t VALUES (3,30,3,0,1)", "CS03038", "positive", "dbo.t")]
    [InlineData(false, "UPDATE t SET d = 0 WHERE id = 1", "CS03038", "positive", "dbo.t")]
    [InlineData(true, "UPDATE t SET d = 0 WHERE id = 1", "CS03038", "positive", "dbo.t")]
    [InlineData(false, "SELECT CAST(0 AS positive) FROM t", "CS03038", "positive", "")]
    [InlineData(true, "SELECT CAST(0 AS positive) FROM t", "CS03038", "positive", "")]
    [InlineData(false, "INSERT INTO t VALUES (3,30,3,3,99)", "CS03039", "fk_t", "dbo.t")]
    [InlineData(true, "INSERT INTO t VALUES (3,30,3,3,99)", "CS03039", "fk_t", "dbo.t")]
    [InlineData(false, "UPDATE t SET pid = 99 WHERE id = 1", "CS03039", "fk_t", "dbo.t")]
    [InlineData(true, "UPDATE t SET pid = 99 WHERE id = 1", "CS03039", "fk_t", "dbo.t")]
    [InlineData(false, "DELETE FROM p WHERE id = 1", "CS03039", "fk_t", "dbo.t")]
    [InlineData(true, "DELETE FROM p WHERE id = 1", "CS03039", "fk_t", "dbo.t")]
    public async Task ExecuteAsync_ConstraintViolation_ShouldRetainDiagnosticAndSession(bool tcp, string sql, string code, string constraint, string table)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync(tcp: tcp);
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        string[] setup =
        [
            "CREATE DOMAIN positive AS INT CHECK(VALUE > 0)",
            "CREATE TABLE p (id INT PRIMARY KEY)",
            "INSERT INTO p VALUES (1)",
            "CREATE TABLE t (id INT, u INT, qty INT, d positive, pid INT, CONSTRAINT pk_t PRIMARY KEY(id), CONSTRAINT uq_d UNIQUE(d), CONSTRAINT ck_t CHECK(qty > 0), CONSTRAINT fk_t FOREIGN KEY(pid) REFERENCES p(id) ON DELETE RESTRICT)",
            "CREATE UNIQUE INDEX uq_t ON t(u)",
            "INSERT INTO t VALUES (1,10,1,1,1),(2,20,2,2,1)",
            "CREATE TABLE source (id INT, u INT, qty INT, d INT, pid INT)",
            "INSERT INTO source VALUES (3,30,3,3,1),(4,10,4,4,1)",
        ];
        foreach (string statement in setup) { await connection.ExecuteAsync(statement, cancellationToken: SqlClientTestHarness.Timeout()); }
        await connection.ExecuteAsync("BEGIN", cancellationToken: SqlClientTestHarness.Timeout());
        await connection.ExecuteAsync("INSERT INTO p VALUES (9)", cancellationToken: SqlClientTestHarness.Timeout());

        // Act
        var failure = await Should.ThrowAsync<SqlClientException>(async () =>
            await connection.QueryAsync("\n  " + sql, cancellationToken: SqlClientTestHarness.Timeout()));

        // Assert
        failure.Kind.ShouldBe(SqlClientErrorKind.ExecutionFailure);
        failure.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        failure.ConnectionUsable.ShouldBeTrue();
        failure.Message.ShouldStartWith(code + ": ");
        var diagnostic = failure.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe(code);
        diagnostic.Severity.ShouldBe(DiagnosticSeverity.Error);
        diagnostic.Start.ShouldBe(3);
        diagnostic.End.ShouldBe(3 + sql.Length);
        diagnostic.Line.ShouldBe(1);
        failure.Message.ShouldBe(code + ": " + diagnostic.Message);
        diagnostic.Message!.ShouldContain(constraint, Case.Sensitive);
        if (table.Length > 0) { diagnostic.Message!.ShouldContain(table, Case.Sensitive); }
        var inner = failure.InnerException.ShouldBeOfType<DatabaseClientException>().Diagnostics.ShouldHaveSingleItem();
        inner.Code.ShouldBe(code);
        inner.Start.ShouldBe(diagnostic.Start);
        inner.End.ShouldBe(diagnostic.End);
        var rows = await connection.QueryAsync("SELECT id, u, qty, d, pid FROM t ORDER BY id", cancellationToken: SqlClientTestHarness.Timeout());
        rows.Count.ShouldBe(2);
        for (int i = 0; i < 2; i++)
        {
            new object?[] { rows[i][0], rows[i][1], rows[i][2], rows[i][3], rows[i][4] }
                .ShouldBe(new object?[] { i + 1, (i + 1) * 10, i + 1, i + 1, 1 });
        }
        await connection.ExecuteAsync("COMMIT", cancellationToken: SqlClientTestHarness.Timeout());
        (await connection.QueryAsync("SELECT id FROM p ORDER BY id", cancellationToken: SqlClientTestHarness.Timeout())).Count.ShouldBe(2);
        harness.Server.Sessions.Count.ShouldBe(1);
    }
}
