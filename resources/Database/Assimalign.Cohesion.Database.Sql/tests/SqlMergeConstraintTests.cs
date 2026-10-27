using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlMergeConstraintTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - MERGE: FK actions read a directly updated child's final reference (#1715)")]
    [InlineData("CASCADE")]
    [InlineData("SET NULL")]
    [InlineData("SET DEFAULT")]
    [InlineData("NO ACTION")]
    [InlineData("RESTRICT")]
    public async Task ExecuteAsync_MovedChild_ShouldComposeBeforeReferentialActions(string policy)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("merge-moved-fk", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await ExecuteAsync(session, "CREATE TABLE t(id INT PRIMARY KEY,pid INT DEFAULT 9 REFERENCES t(id) ON DELETE " + policy + ",v INT)", timeout.Token);
        await ExecuteAsync(session, "INSERT INTO t VALUES(1,NULL,10),(2,1,20),(9,NULL,90)", timeout.Token);

        // Act
        var result = await session.ExecuteAsync("MERGE INTO t USING (SELECT 1 AS id UNION ALL SELECT 2) AS s ON t.id=s.id " +
            "WHEN MATCHED AND t.id=1 THEN DELETE WHEN MATCHED THEN UPDATE SET pid=9,v=t.v+1 RETURNING t.id,t.pid,t.v,merge_action()", cancellationToken: timeout.Token);

        // Assert
        result.AffectedCount.ShouldBe(2);
        (await ReadAsync(result, timeout.Token)).Order().ShouldBe(["1,,10,DELETE", "2,9,21,UPDATE"]);
        (await QueryAsync(session, "SELECT id,pid,v FROM t ORDER BY id", timeout.Token)).ShouldBe(["2,9,21", "9,,90"]);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - MERGE: direct and referential writes overlap without a false repeated-target failure (#1715)")]
    [InlineData("CASCADE", false, "")]
    [InlineData("SET NULL", false, "2,,21")]
    [InlineData("SET DEFAULT", false, "2,9,21")]
    [InlineData("CASCADE", true, "")]
    [InlineData("SET NULL", true, "3,,30")]
    [InlineData("SET DEFAULT", true, "3,9,30")]
    public async Task ExecuteAsync_OverlappingReferentialWrite_ShouldComposeFinalRows(string policy, bool insert, string child)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("merge-overlap", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await ExecuteAsync(session, "CREATE TABLE t(id INT PRIMARY KEY,pid INT DEFAULT 9 REFERENCES t(id) ON DELETE " + policy + ",v INT)", timeout.Token);
        await ExecuteAsync(session, "INSERT INTO t VALUES(1,NULL,10),(9,NULL,90)" + (insert ? "" : ",(2,1,20)"), timeout.Token);

        // Act
        var result = await session.ExecuteAsync("MERGE INTO t USING (SELECT 1 AS id UNION ALL SELECT " + (insert ? "3" : "2") + ") AS s ON t.id=s.id " +
            "WHEN MATCHED AND t.id=1 THEN DELETE WHEN MATCHED THEN UPDATE SET v=t.v+1 " +
            "WHEN NOT MATCHED THEN INSERT VALUES(s.id,1,30) RETURNING t.id,t.pid,t.v,merge_action()", cancellationToken: timeout.Token);

        // Assert: RETURNING describes direct actions before AFTER-style cascades.
        result.AffectedCount.ShouldBe(2);
        (await ReadAsync(result, timeout.Token)).Order().ShouldBe(insert
            ? ["1,,10,DELETE", "3,1,30,INSERT"] : ["1,,10,DELETE", "2,1,21,UPDATE"]);
        (await QueryAsync(session, "SELECT id,pid,v FROM t ORDER BY id", timeout.Token)).ShouldBe(
            child.Length == 0 ? ["9,,90"] : new[] { child, "9,,90" });
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - MERGE: replacement parent keys satisfy NO ACTION at final state (#1715)")]
    [InlineData("NO ACTION", true)]
    [InlineData("RESTRICT", false)]
    public async Task ExecuteAsync_ReplacedParent_ShouldRespectReferentialPolicy(string policy, bool success)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("merge-parent-replacement", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await ExecuteAsync(session, "CREATE TABLE t(id INT PRIMARY KEY,v INT)", timeout.Token);
        await ExecuteAsync(session, "CREATE TABLE child(id INT,pid INT REFERENCES t(id) ON DELETE " + policy + ")", timeout.Token);
        await ExecuteAsync(session, "INSERT INTO t VALUES(1,10)", timeout.Token);
        await ExecuteAsync(session, "INSERT INTO child VALUES(2,1)", timeout.Token);
        const string sql = "MERGE INTO t USING (SELECT 1 AS id UNION ALL SELECT 8) AS s ON t.id=s.id " +
            "WHEN MATCHED THEN DELETE WHEN NOT MATCHED THEN INSERT VALUES(1,99)";

        // Act / Assert
        if (success)
        {
            (await session.ExecuteAsync(sql, cancellationToken: timeout.Token)).AffectedCount.ShouldBe(2);
            (await QueryAsync(session, "SELECT id,v FROM t", timeout.Token)).ShouldBe(["1,99"]);
        }
        else
        {
            var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(sql, cancellationToken: timeout.Token));
            failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS03039");
            (await QueryAsync(session, "SELECT id,v FROM t", timeout.Token)).ShouldBe(["1,10"]);
        }
        (await QueryAsync(session, "SELECT id,pid FROM child", timeout.Token)).ShouldBe(["2,1"]);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - MERGE: cascade of a rekeyed row uses its effective key and removes its stored index key (#1715)")]
    public async Task ExecuteAsync_RekeyThenCascade_ShouldUseEffectiveReferenceKeys()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("merge-rekey-cascade", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await ExecuteAsync(session, "CREATE TABLE t(id INT PRIMARY KEY,pid INT REFERENCES t(id) ON DELETE CASCADE,sid INT REFERENCES t(id) ON DELETE SET NULL)", timeout.Token);
        await ExecuteAsync(session, "INSERT INTO t VALUES(1,NULL,NULL),(2,1,NULL),(4,NULL,2)", timeout.Token);

        // Act
        var result = await session.ExecuteAsync("MERGE INTO t USING (SELECT 1 AS id UNION ALL SELECT 2 UNION ALL SELECT 4) AS s ON t.id=s.id " +
            "WHEN MATCHED AND t.id=1 THEN DELETE WHEN MATCHED AND t.id=2 THEN UPDATE SET id=3 " +
            "WHEN MATCHED THEN UPDATE SET sid=3", cancellationToken: timeout.Token);

        // Assert
        result.AffectedCount.ShouldBe(3);
        (await QueryAsync(session, "SELECT id,pid,sid FROM t", timeout.Token)).ShouldBe(["4,,"]);
        await ExecuteAsync(session, "INSERT INTO t VALUES(2,NULL,NULL),(3,NULL,NULL)", timeout.Token);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - MERGE: cascades cannot hide immediate direct-row constraints (#1715)")]
    [InlineData("WHEN MATCHED THEN UPDATE SET v=-1", "2", "CS03038")]
    [InlineData("WHEN NOT MATCHED THEN INSERT VALUES(2,1,1)", "3 UNION ALL SELECT 4", "CS03037")]
    public async Task ExecuteAsync_InvalidCascadedProposal_ShouldRefuseAtomically(string action, string source, string code)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("merge-immediate-checks", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await ExecuteAsync(session, "CREATE TABLE t(id INT PRIMARY KEY,pid INT REFERENCES t(id) ON DELETE CASCADE,v INT CHECK(v>0))", timeout.Token);
        await ExecuteAsync(session, "INSERT INTO t VALUES(1,NULL,1),(2,1,1)", timeout.Token);

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            "MERGE INTO t USING (SELECT 1 AS id UNION ALL SELECT " + source + ") AS s ON t.id=s.id " +
            "WHEN MATCHED AND t.id=1 THEN DELETE " + action, cancellationToken: timeout.Token));

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
        (await QueryAsync(session, "SELECT id,pid,v FROM t ORDER BY id", timeout.Token)).ShouldBe(["1,,1", "2,1,1"]);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - MERGE: mixed updates and inserts provide final self-reference keys (#1715)")]
    public async Task ExecuteAsync_InsertedReplacementParent_ShouldSatisfyUpdatedChildren()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("merge-final-self", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await ExecuteAsync(session, "CREATE TABLE t(id INT PRIMARY KEY,pid INT REFERENCES t(id))", timeout.Token);
        await ExecuteAsync(session, "INSERT INTO t VALUES(1,NULL),(2,1)", timeout.Token);

        // Act
        (await session.ExecuteAsync("MERGE INTO t USING (SELECT 1 AS id UNION ALL SELECT 3) AS s ON t.id=s.id " +
            "WHEN MATCHED THEN UPDATE SET id=4 WHEN NOT MATCHED THEN INSERT VALUES(1,NULL)", cancellationToken: timeout.Token)).AffectedCount.ShouldBe(2);

        // Assert
        (await QueryAsync(session, "SELECT id,pid FROM t ORDER BY id", timeout.Token)).ShouldBe(["1,", "2,1", "4,"]);
    }

    private static async Task ExecuteAsync(SqlDatabaseSession session, string sql, CancellationToken token)
        => (await session.ExecuteAsync(sql, cancellationToken: token)).Status.ShouldBe(DatabaseResultStatus.Success);

    private static async Task<string[]> QueryAsync(SqlDatabaseSession session, string sql, CancellationToken token)
        => await ReadAsync(await session.ExecuteAsync(sql, cancellationToken: token), token);

    private static async Task<string[]> ReadAsync(DatabaseResult result, CancellationToken token)
    {
        var rows = new System.Collections.Generic.List<string>();
        await using var set = result.ShouldBeAssignableTo<DatabaseResultSet>();
        await foreach (var row in set.GetRowsAsync(token))
        { rows.Add(string.Join(',', Enumerable.Range(0, row.FieldCount).Select(row.GetValue))); }
        return rows.ToArray();
    }
}
