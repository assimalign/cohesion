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
using Assimalign.Cohesion.Database.Sql.Schema;
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
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS01015");
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
        diagnostic.Code.ShouldBe("CS01015");
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
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS01015");
        failure.Message.ShouldContain("nosuch.a", Case.Sensitive);
        failure.ConnectionUsable.ShouldBeTrue();
        await connection.ExecuteAsync("INSERT INTO t VALUES (1)", cancellationToken: TestTimeout.Token());
        (await connection.QueryAsync("SELECT id FROM t", cancellationToken: TestTimeout.Token())).ShouldHaveSingleItem()["id"].ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Qualifiers: an applied schema restarts leniently and a changed hash is refused (#1191)")]
    [InlineData("apply")]
    [InlineData("verify")]
    [InlineData("imperative")]
    public async Task BuildAsync_PreviouslyAppliedCheckQualifier_ShouldStartAndRejectChangedSchema(string mode)
    {
        // Arrange: seed the schema-owned catalog and applied marker an earlier engine accepted.
        string directory = Path.Combine(Path.GetTempPath(), "cohesion-schema-qualifier", Guid.NewGuid().ToString("N"));
        var declaration = QualifierSchema(changed: false);
        var schema = declaration.Compile();
        try
        {
            await using (var engine = SqlDatabaseEngine.Create("schema-check", new() { RootPath = directory }))
            {
                var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
                await database.Catalog.CreateTableAsync("dbo", "t", [new SqlCatalogColumn("A", new DatabaseTypeInfo(DatabaseType.Int32), false)], [], DatabaseObjectOwner.Schema, "db",
                    constraints: [new SqlCatalogConstraint("ck_old", SqlCatalogConstraintKind.Check, ["A"], checkExpression: "nosuch.a > 0")], cancellationToken: CancellationToken.None);
                await database.Catalog.SaveSchemaStateAsync(new SqlCatalogSchemaState(schema.Hash, schema.CanonicalDocument, database.Catalog.DdlGeneration), CancellationToken.None);
            }

            // Act
            var builder = SqlDatabaseEngine.CreateBuilder("schema-check");
            builder.Options.RootPath = directory;
            if (mode != "imperative")
            {
                builder.AddDatabase("db", database =>
                {
                    database.Schema(declaration);
                    database.Provisioning = mode == "verify" ? SqlProvisioningMode.Verify : SqlProvisioningMode.Apply;
                });
            }
            await using (var reopenedEngine = await builder.BuildAsync(CancellationToken.None))
            {
                var reopened = reopenedEngine.TryGetDatabase("db", out var opened)
                    ? opened! : await reopenedEngine.OpenDatabaseAsync("db", CancellationToken.None);
                var result = mode == "imperative"
                    ? await reopened.ApplySchemaAsync(schema, CancellationToken.None)
                    : reopenedEngine.DeclaredDatabases.ShouldHaveSingleItem().Result.ShouldNotBeNull();

                // Assert: startup/apply skipped DDL, and the stored CHECK still enforces.
                result.WasAlreadyApplied.ShouldBeTrue();
                result.OperationCount.ShouldBe(0);
                await using var session = await reopened.CreateSessionAsync(CancellationToken.None);
                await session.ExecuteAsync("INSERT INTO t VALUES (1)", cancellationToken: CancellationToken.None);
                var invalid = await SqlConstraintAssertions.ThrowAsync(async () =>
                    await session.ExecuteAsync("INSERT INTO t VALUES (-1)", cancellationToken: CancellationToken.None));
                invalid.ConstraintName.ShouldBe("ck_old");
                if (mode == "imperative")
                {
                    var changed = await Should.ThrowAsync<SqlSchemaMigrationException>(async () =>
                        await reopened.ApplySchemaAsync(QualifierSchema(changed: true).Compile(), CancellationToken.None));
                    changed.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS01015");
                }
                reopened.Catalog.SchemaState.ShouldNotBeNull().ContentHash.ShouldBe(schema.Hash);
            }

            var changedBuilder = SqlDatabaseEngine.CreateBuilder("schema-check");
            changedBuilder.Options.RootPath = directory;
            changedBuilder.AddDatabase(QualifierSchema(changed: true));
            var refused = await Should.ThrowAsync<SqlSchemaMigrationException>(async () =>
                await changedBuilder.BuildAsync(CancellationToken.None));
            refused.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS01015");
            refused.Message.ShouldContain("nosuch.a", Case.Sensitive);
        }
        finally
        {
            if (Directory.Exists(directory)) { Directory.Delete(directory, recursive: true); }
        }
    }

    private static SqlSchema QualifierSchema(bool changed)
        => SqlSchema.Create("db", schema => schema.Table<QualifierRow>("t", table =>
        {
            table.Column(row => row.A);
            table.Check("ck_old", changed ? "nosuch.a > 1" : "nosuch.a > 0");
        }));

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
