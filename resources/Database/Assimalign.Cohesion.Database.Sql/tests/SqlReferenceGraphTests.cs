using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlReferenceGraphTests
{
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Reference graph: multi-level mixed delete actions share each incoming list (#1198)")]
    [InlineData("SET NULL")]
    [InlineData("SET DEFAULT")]
    public async Task Delete_MultiLevelMixedActions_ShouldPrepareEachReachedTableOnce(string action)
    {
        // Arrange: A references B, which references C.
        await using var engine = SqlDatabaseEngine.Create("reference-levels", new());
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE c (id INT PRIMARY KEY)");
        await session.ExecuteAsync("CREATE TABLE b (id INT PRIMARY KEY, cid INT REFERENCES c(id) ON DELETE CASCADE)");
        await session.ExecuteAsync($"CREATE TABLE a (id INT PRIMARY KEY, bid INT DEFAULT NULL REFERENCES b(id) ON DELETE {action})");
        await session.ExecuteAsync("INSERT INTO c VALUES (1)");
        await session.ExecuteAsync("INSERT INTO b VALUES (1,1)");
        await session.ExecuteAsync("INSERT INTO a VALUES (1,1),(2,1),(3,1)");

        // Act
        (await session.ExecuteAsync("DELETE FROM c")).AffectedCount.ShouldBe(1);
        var metrics = session.LastStatementMetrics.ShouldNotBeNull();

        // Assert
        metrics.IncomingReferenceBuilds.ShouldBe(3);
        metrics.ReferenceCatalogTableVisits.ShouldBe(9);
        metrics.ReferenceIntentLockBatches.ShouldBe(6);
        (await session.ExecuteAsync("DELETE FROM a WHERE bid IS NULL")).AffectedCount.ShouldBe(3);
        (await session.ExecuteAsync("DELETE FROM b")).AffectedCount.ShouldBe(0);
        (await session.ExecuteAsync("DELETE FROM c")).AffectedCount.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Reference graph: mixed cyclic actions share lists and preserve final state (#1198)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Delete_CyclicMixedActions_ShouldPrepareEachTableOnce(bool deleteA)
    {
        // Arrange: A references B with SET NULL; B references A with CASCADE.
        await using var engine = SqlDatabaseEngine.Create("reference-cycle", new());
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE a (id INT PRIMARY KEY, bid INT)");
        await session.ExecuteAsync("CREATE TABLE b (id INT PRIMARY KEY, aid INT REFERENCES a(id) ON DELETE CASCADE)");
        await session.ExecuteAsync("ALTER TABLE a ADD CONSTRAINT fk FOREIGN KEY(bid) REFERENCES b(id) ON DELETE SET NULL");
        await session.ExecuteAsync("INSERT INTO a VALUES (1,NULL)");
        await session.ExecuteAsync("INSERT INTO b VALUES (1,1)");
        await session.ExecuteAsync("UPDATE a SET bid = 1");

        // Act
        (await session.ExecuteAsync(deleteA ? "DELETE FROM a" : "DELETE FROM b")).AffectedCount.ShouldBe(1);
        var metrics = session.LastStatementMetrics.ShouldNotBeNull();

        // Assert
        metrics.IncomingReferenceBuilds.ShouldBe(2);
        metrics.ReferenceCatalogTableVisits.ShouldBe(4);
        metrics.ReferenceIntentLockBatches.ShouldBe(4);
        (await session.ExecuteAsync("DELETE FROM a WHERE bid IS NULL")).AffectedCount.ShouldBe(deleteA ? 0 : 1);
        (await session.ExecuteAsync("DELETE FROM b")).AffectedCount.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Reference graph: referenced-key UPDATE validates the cached incoming list (#1198)")]
    public async Task Update_ReferencedKey_ShouldCheckIncomingReferencesAndRemainAtomic()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("reference-key-update", new());
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE p (id INT PRIMARY KEY)");
        await session.ExecuteAsync("CREATE TABLE c (pid INT, CONSTRAINT fk FOREIGN KEY(pid) REFERENCES p(id))");
        await session.ExecuteAsync("INSERT INTO p VALUES (1),(2)");
        await session.ExecuteAsync("INSERT INTO c VALUES (1)");

        // Act
        var failure = await SqlConstraintAssertions.ThrowAsync(() => session.ExecuteAsync("UPDATE p SET id = id + 10").AsTask());
        var metrics = session.LastStatementMetrics.ShouldNotBeNull();

        // Assert
        failure.ConstraintName.ShouldBe("fk");
        metrics.IncomingReferenceBuilds.ShouldBe(1);
        metrics.ReferenceCatalogTableVisits.ShouldBe(2);
        metrics.ReferenceIntentLockBatches.ShouldBe(2);
        metrics.ConstraintLookupBuilds.ShouldBe(1);
        (await session.ExecuteAsync("DELETE FROM c")).AffectedCount.ShouldBe(1);
        (await session.ExecuteAsync("UPDATE p SET id = id + 10")).AffectedCount.ShouldBe(2);
        (await session.ExecuteAsync("DELETE FROM p WHERE id IN (11,12)")).AffectedCount.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Reference graph: outgoing parent probes reuse their statement lookup (#1366)")]
    public async Task Insert_RepeatedOutgoingParent_ShouldBuildOneLookup()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("reference-outgoing", new());
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE p (id INT PRIMARY KEY)");
        await session.ExecuteAsync("CREATE TABLE c (id INT PRIMARY KEY, pid INT REFERENCES p(id))");
        await session.ExecuteAsync("INSERT INTO p VALUES (1)");

        // Act
        (await session.ExecuteAsync("INSERT INTO c VALUES (1,1),(2,1),(3,1),(4,NULL)")).AffectedCount.ShouldBe(4);
        var metrics = session.LastStatementMetrics.ShouldNotBeNull();

        // Assert
        metrics.IncomingReferenceBuilds.ShouldBe(0);
        metrics.ReferenceCatalogTableVisits.ShouldBe(0);
        metrics.ReferenceIntentLockBatches.ShouldBe(1);
        metrics.ConstraintLookupBuilds.ShouldBe(1);
        (await session.ExecuteAsync("DELETE FROM c WHERE pid = 1")).AffectedCount.ShouldBe(3);
        (await session.ExecuteAsync("DELETE FROM c WHERE pid IS NULL")).AffectedCount.ShouldBe(1);
    }
}
