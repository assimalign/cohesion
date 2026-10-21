using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Client;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>Stored-table query and CHECK declaration qualifier conformance (#1191).</summary>
public sealed class SqlQualifierConformanceTests
{
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Qualifiers: every stored-table query clause rejects an unknown relation (#1191)")]
    [InlineData("SELECT nosuch.a FROM t")]
    [InlineData("SELECT a FROM t WHERE nosuch.a > 0")]
    [InlineData("SELECT COUNT(*) FROM t GROUP BY nosuch.a")]
    [InlineData("SELECT a FROM t GROUP BY a HAVING nosuch.a > 0")]
    [InlineData("SELECT a FROM t ORDER BY nosuch.a")]
    [InlineData("SELECT t.a FROM t AS x")]
    [InlineData("SELECT (SELECT nosuch.a FROM t AS inner_t) FROM t")]
    public async Task ExecuteAsync_UnknownQueryQualifier_ShouldReject(string sql)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("query-qualifiers", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (a INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES (1)", cancellationToken: CancellationToken.None);

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None));

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHSQLE015");
        failure.Message.ShouldContain(sql.Contains("nosuch") ? "nosuch.a" : "t.a", Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Qualifiers: CHECK declarations reject a wrong table or schema before publication (#1191)")]
    [InlineData("CREATE TABLE d (a INT CHECK(nosuch.a > 0))")]
    [InlineData("CREATE TABLE d (a INT, CHECK(nosuch.a > 0))")]
    [InlineData("CREATE TABLE d (a INT, CHECK(missing.d.a > 0))")]
    [InlineData("ALTER TABLE t ADD CONSTRAINT ck_bad CHECK(nosuch.a > 0)")]
    [InlineData("ALTER TABLE t ADD COLUMN extra INT CHECK(nosuch.extra > 0)")]
    public async Task ExecuteAsync_UnknownCheckQualifier_ShouldReject(string sql)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("check-qualifiers", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (a INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES (1)", cancellationToken: CancellationToken.None);

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None));

        // Assert
        var diagnostic = failure.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("COHSQLE015");
        diagnostic.Start.ShouldNotBeNull();
        failure.Message.ShouldContain(sql.Contains("missing") ? "missing.d.a" : sql.Contains("extra") ? "nosuch.extra" : "nosuch.a", Case.Sensitive);
        await session.ExecuteAsync("CREATE TABLE d (a INT CHECK(d.a > 0 AND dbo.d.a < 100))", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("ALTER TABLE t ADD COLUMN extra INT CHECK(t.extra > 0)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("ALTER TABLE t ADD CONSTRAINT ck_good CHECK(dbo.t.a > 0)", cancellationToken: CancellationToken.None);
        await SqlConstraintAssertions.ThrowAsync(async () => await session.ExecuteAsync("UPDATE t SET a = -1", cancellationToken: CancellationToken.None));
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Qualifiers: CHECK refusal over the wire keeps the connection usable (#1191)")]
    [InlineData("CREATE TABLE d (a INT CHECK(nosuch.a > 0))")]
    [InlineData("ALTER TABLE t ADD CONSTRAINT ck_bad CHECK(nosuch.a > 0)")]
    [InlineData("ALTER TABLE t ADD COLUMN a INT CHECK(nosuch.a > 0)")]
    public async Task ExecuteAsync_UnknownCheckQualifierOverWire_ShouldReject(string sql)
    {
        // Arrange
        await using var harness = await ServerTestHarness.StartAsync();
        await using var client = SqlClient.Create(new SqlClientOptions
        {
            Settings = new DatabaseConnectionSettings { Database = ServerTestHarness.DatabaseName, Principal = "check-qualifiers", EndPoint = harness.Listener.EndPoint },
            ConnectionFactory = harness.Listener.CreateFactory(),
        });
        await using var connection = await client.ConnectAsync(TestTimeout.Token());
        await connection.ExecuteAsync("CREATE TABLE t (id INT)", cancellationToken: TestTimeout.Token());

        // Act
        var failure = await Should.ThrowAsync<SqlClientException>(async () => await connection.ExecuteAsync(sql, cancellationToken: TestTimeout.Token()));

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHSQLE015");
        failure.Message.ShouldContain("nosuch.a", Case.Sensitive);
        failure.ConnectionUsable.ShouldBeTrue();
        await connection.ExecuteAsync("INSERT INTO t VALUES (1)", cancellationToken: TestTimeout.Token());
        (await connection.QueryAsync("SELECT id FROM t", cancellationToken: TestTimeout.Token())).ShouldHaveSingleItem()["id"].ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Qualifiers: a persisted CHECK accepted by an earlier engine still opens and enforces (#1191)")]
    public async Task OpenDatabaseAsync_PreviouslyAcceptedCheckQualifier_ShouldKeepBinding()
    {
        // Arrange: catalog insertion represents the predicate an earlier engine persisted.
        string directory = Path.Combine(Path.GetTempPath(), "cohesion-check-qualifier", Guid.NewGuid().ToString("N"));
        try
        {
            await using (var engine = SqlDatabaseEngine.Create("persisted-check", new() { RootPath = directory }))
            {
                var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
                await database.Catalog.CreateTableAsync("dbo", "t", [new SqlCatalogColumn("a", new DatabaseTypeInfo(DatabaseType.Int32), true)], [], DatabaseObjectOwner.Adhoc, null,
                    constraints: [new SqlCatalogConstraint("ck_old", SqlCatalogConstraintKind.Check, ["a"], checkExpression: "nosuch.a > 0")], cancellationToken: CancellationToken.None);
            }

            // Act
            await using var reopenedEngine = SqlDatabaseEngine.Create("persisted-check", new() { RootPath = directory });
            var reopened = await reopenedEngine.OpenDatabaseAsync("db", CancellationToken.None);
            await using var session = await reopened.CreateSessionAsync(CancellationToken.None);
            await session.ExecuteAsync("INSERT INTO t VALUES (1)", cancellationToken: CancellationToken.None);
            var failure = await SqlConstraintAssertions.ThrowAsync(async () => await session.ExecuteAsync("INSERT INTO t VALUES (-1)", cancellationToken: CancellationToken.None));

            // Assert
            failure.ConstraintName.ShouldBe("ck_old");
            await using var rows = (await session.ExecuteAsync("SELECT a FROM t", cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
            (await SqlT1ScalarTests.ReadAsync(rows)).ShouldHaveSingleItem()[0].ShouldBe(1);
        }
        finally
        {
            if (Directory.Exists(directory)) { Directory.Delete(directory, recursive: true); }
        }
    }
}
