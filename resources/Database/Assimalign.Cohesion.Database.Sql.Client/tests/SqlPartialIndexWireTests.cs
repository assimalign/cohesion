using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

public sealed class SqlPartialIndexWireTests
{
    [Theory(DisplayName = "Cohesion Test [Sql.Client] - Partial implication: inference and EXPLAIN proof cross InMemory and TCP (#1630)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_InferredPartialArbiter_ShouldPreserveProofAndMembership(bool tcp)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync(tcp: tcp);
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout(30));
        await connection.ExecuteAsync("CREATE TABLE t(k INT,b BOOLEAN,n INT)", cancellationToken: SqlClientTestHarness.Timeout(30));
        await connection.ExecuteAsync("CREATE UNIQUE INDEX uq ON t(k) WHERE b AND n>0", cancellationToken: SqlClientTestHarness.Timeout(30));
        await connection.ExecuteAsync("INSERT INTO t VALUES(7,TRUE,1),(7,FALSE,1)", cancellationToken: SqlClientTestHarness.Timeout(30));

        // Act
        await connection.ExecuteAsync("INSERT INTO t VALUES(7,TRUE,2) ON CONFLICT(k) WHERE b=TRUE AND n>=1 DO UPDATE SET n=excluded.n", cancellationToken: SqlClientTestHarness.Timeout(30));
        var explained = await connection.QueryAsync("EXPLAIN SELECT k FROM t WHERE k=7 AND b AND n>=1", cancellationToken: SqlClientTestHarness.Timeout(30));
        var failure = await Should.ThrowAsync<SqlClientException>(async () => await connection.ExecuteAsync(
            "INSERT INTO t VALUES(7,TRUE,3) ON CONFLICT(k) DO NOTHING", cancellationToken: SqlClientTestHarness.Timeout(30)));

        // Assert
        explained.ShouldContain(row => Equals(row[4], "uq") && row[5]!.ToString()!.Contains("implied_by="));
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS03035");
        failure.ConnectionUsable.ShouldBeTrue();
        (await connection.QueryAsync("SELECT n FROM t WHERE b", cancellationToken: SqlClientTestHarness.Timeout(30))).Single()[0].ShouldBe(2);
        (await connection.QueryAsync("SELECT k FROM t WHERE k=7", cancellationToken: SqlClientTestHarness.Timeout(30))).Count.ShouldBe(2);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Client] - Partial indexes: subset uniqueness and canonical metadata over InMemory and TCP (#1630)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_PartialUnique_ShouldMaintainSubsetAndReturnCodedRefusals(bool tcp)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync(tcp: tcp);
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout(30));
        await connection.ExecuteAsync("CREATE TABLE t(id INT,k INT,active BOOLEAN)", cancellationToken: SqlClientTestHarness.Timeout(30));
        await connection.ExecuteAsync("INSERT INTO t VALUES(1,7,TRUE),(2,7,FALSE),(3,NULL,TRUE),(4,NULL,TRUE)", cancellationToken: SqlClientTestHarness.Timeout(30));
        await connection.ExecuteAsync("CREATE UNIQUE INDEX uq ON t(k) WHERE active", cancellationToken: SqlClientTestHarness.Timeout(30));

        // Act
        var failure = await Should.ThrowAsync<SqlClientException>(async () => await connection.ExecuteAsync(
            "UPDATE t SET active=TRUE WHERE id=2", cancellationToken: SqlClientTestHarness.Timeout(30)));
        var invalid = await Should.ThrowAsync<SqlClientException>(async () => await connection.ExecuteAsync(
            "CREATE INDEX bad ON t(k) WHERE nextval('s')>0", cancellationToken: SqlClientTestHarness.Timeout(30)));
        await connection.ExecuteAsync("UPDATE t SET active=FALSE WHERE id=1", cancellationToken: SqlClientTestHarness.Timeout(30));
        await connection.ExecuteAsync("UPDATE t SET active=TRUE WHERE id=2", cancellationToken: SqlClientTestHarness.Timeout(30));
        await connection.ExecuteAsync("CREATE VIEW predicates AS SELECT PREDICATE FROM INFORMATION_SCHEMA.INDEXES WHERE INDEX_NAME='uq'", cancellationToken: SqlClientTestHarness.Timeout(30));

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS03037");
        invalid.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS05091");
        failure.ConnectionUsable.ShouldBeTrue();
        (await connection.QueryAsync("SELECT PREDICATE FROM predicates", cancellationToken: SqlClientTestHarness.Timeout(30))).Single()[0].ShouldBe("active");
        (await connection.QueryAsync("SELECT k FROM t WHERE k=7", cancellationToken: SqlClientTestHarness.Timeout(30))).Count.ShouldBe(2);
    }
}
