using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlSystemRelationJoinTests
{
    public static IEnumerable<object[]> Relations()
        => SqlSystemViews.Relations.Select(view => new object[] { view.Schema, view.Name, JoinKey(view.Name, view.Columns[0].Name) });

    private static string JoinKey(string view, string firstColumn) => view switch
    {
        "FUNCTIONS" or "TYPES" or "DOMAINS" => "IS_SQL_DEFINED",
        _ => firstColumn,
    };

    [Theory(DisplayName = "Cohesion Test [Sql] - System relations: every registered source supports self and stored joins")]
    [MemberData(nameof(Relations))]
    public async Task Join_EverySystemRelation_ShouldUseTheSharedSourceAsync(string schema, string name, string key)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("catalog-joins", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await SeedAsync(session);
        var source = await RowsAsync(session, $"SELECT {key} FROM {schema}.{name}");
        source.ShouldNotBeEmpty();
        var keys = source.Select(row => (string)row[0]!).Distinct().OrderBy(value => value, StringComparer.Ordinal).ToArray();
        await session.ExecuteAsync("CREATE TABLE probes (key_value TEXT)", cancellationToken: CancellationToken.None);
        foreach (string value in keys)
        {
            await session.ExecuteAsync("INSERT INTO probes VALUES (@key)",
                new Dictionary<string, object?> { ["key"] = value }, CancellationToken.None);
        }
        // probes adds metadata rows to some views, so read the complete expected image after seeding.
        source = await RowsAsync(session, $"SELECT {key} FROM {schema}.{name}");
        var expected = source.GroupBy(row => (string)row[0]!).OrderBy(group => group.Key, StringComparer.Ordinal)
            .SelectMany(group => Enumerable.Repeat(group.Key, group.Count() * group.Count())).ToArray();

        // Act
        var self = await RowsAsync(session,
            $"SELECT a.{key} FROM {schema}.{name} a JOIN {schema}.{name} b ON a.{key} = b.{key} ORDER BY a.{key}");
        session.LastStatementMetrics!.CatalogSnapshotCaptures.ShouldBe(1);
        var stored = await RowsAsync(session,
            $"SELECT a.{key} FROM {schema}.{name} a JOIN probes p ON a.{key} = p.key_value ORDER BY a.{key}");

        // Assert: catalog-name-first relations yield n*n; other keys preserve their equality partitions.
        self.Select(row => (string)row[0]!).ShouldBe(expected);
        stored.Select(row => (string)row[0]!).ShouldBe(source.Select(row => (string)row[0]!).OrderBy(value => value, StringComparer.Ordinal));
        session.LastStatementMetrics!.CatalogSnapshotCaptures.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - System relations: inventory and composite constraint joins return exact ordered rows")]
    public async Task Join_CatalogRelationships_ShouldReturnOrderedColumnsAsync()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("catalog-relationships", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await SeedAsync(session);

        // Act / Assert
        Check(await RowsAsync(session,
            "SELECT t.TABLE_NAME, INFORMATION_SCHEMA.COLUMNS.COLUMN_NAME, INFORMATION_SCHEMA.COLUMNS.ORDINAL_POSITION " +
            "FROM INFORMATION_SCHEMA.TABLES t JOIN INFORMATION_SCHEMA.COLUMNS ON t.TABLE_NAME = INFORMATION_SCHEMA.COLUMNS.TABLE_NAME " +
            "WHERE t.TABLE_NAME = 'parent' ORDER BY INFORMATION_SCHEMA.COLUMNS.ORDINAL_POSITION"),
            [["parent", "a", 1L], ["parent", "b", 2L]]);
        Check(await RowsAsync(session,
            "SELECT k.COLUMN_NAME, k.ORDINAL_POSITION FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS t " +
            "JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE k ON t.CONSTRAINT_NAME = k.CONSTRAINT_NAME " +
            "WHERE t.CONSTRAINT_NAME = 'pk_parent' ORDER BY k.ORDINAL_POSITION"), [["b", 1L], ["a", 2L]]);
        Check(await RowsAsync(session,
            "SELECT k.COLUMN_NAME, k.ORDINAL_POSITION FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS r " +
            "JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE k ON r.CONSTRAINT_NAME = k.CONSTRAINT_NAME ORDER BY k.ORDINAL_POSITION"),
            [["parent_b", 1L], ["parent_a", 2L]]);
        Check(await RowsAsync(session,
            "SELECT DISTINCT t.TABLE_NAME FROM INFORMATION_SCHEMA.TABLES t JOIN INFORMATION_SCHEMA.COLUMNS c " +
            "ON t.TABLE_NAME = c.TABLE_NAME WHERE c.ORDINAL_POSITION > 0 ORDER BY t.TABLE_NAME LIMIT 1 OFFSET 1"), [["parent"]]);
        Check(await RowsAsync(session,
            "SELECT t.TABLE_NAME, COUNT(*) AS n FROM INFORMATION_SCHEMA.TABLES t JOIN INFORMATION_SCHEMA.COLUMNS c " +
            "ON t.TABLE_NAME = c.TABLE_NAME WHERE c.ORDINAL_POSITION > 0 GROUP BY t.TABLE_NAME HAVING COUNT(*) > 1 " +
            "ORDER BY n DESC LIMIT 1 OFFSET 1"), [["parent", 2L]]);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - System relations: stored index remains usable on either logical side")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Join_StoredIndex_ShouldProbeFromSystemRowsAsync(bool storedFirst)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("catalog-index", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await SeedAsync(session);
        await session.ExecuteAsync("CREATE TABLE names (name TEXT, id INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO names VALUES ('parent', 1), ('child', 2), ('missing', 3)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE INDEX by_name ON names(name)", cancellationToken: CancellationToken.None);
        string from = storedFirst ? "names n JOIN INFORMATION_SCHEMA.TABLES t" : "INFORMATION_SCHEMA.TABLES t JOIN names n";
        string query = $"SELECT n.id, t.TABLE_NAME FROM {from} ON n.name = t.TABLE_NAME ORDER BY n.id";

        // Act
        var sought = await RowsAsync(session, query);
        session.LastStatementMetrics!.JoinAccessPaths.ShouldBe(["join-seek:by_name"]);
        session.LastStatementMetrics.CatalogSnapshotCaptures.ShouldBe(1);
        await session.ExecuteAsync("DROP INDEX by_name ON names", cancellationToken: CancellationToken.None);
        var scanned = await RowsAsync(session, query);

        // Assert
        session.LastStatementMetrics!.JoinAccessPaths.ShouldBe(["join-hash"]);
        Check(sought, [[1, "parent"], [2, "child"]]);
        Check(scanned, sought);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - System relations: ambiguity is a binding error even on empty inputs")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Join_AmbiguousColumn_ShouldFailBeforeReadingAsync(bool empty)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("catalog-ambiguity", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        if (!empty) { await SeedAsync(session); }

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            "SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES t JOIN INFORMATION_SCHEMA.COLUMNS c ON t.TABLE_NAME = c.TABLE_NAME",
            cancellationToken: CancellationToken.None));

        // Assert
        error.Diagnostics.Single().Code.ShouldBe("COHSQLE015");
        error.Message.ShouldContain("Ambiguous column");
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - System relations: every executable position captures exactly once")]
    [InlineData("SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'parent'", "parent")]
    [InlineData("SELECT n.name FROM names n JOIN INFORMATION_SCHEMA.TABLES t ON n.name = t.TABLE_NAME", "parent")]
    [InlineData("SELECT n.name FROM INFORMATION_SCHEMA.TABLES t JOIN names n ON n.name = t.TABLE_NAME", "parent")]
    [InlineData("SELECT n.name FROM names n JOIN names m ON n.name = m.name AND EXISTS (SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES)", "parent")]
    [InlineData("SELECT (SELECT t.TABLE_NAME FROM INFORMATION_SCHEMA.TABLES t JOIN INFORMATION_SCHEMA.COLUMNS c ON t.TABLE_NAME = c.TABLE_NAME WHERE t.TABLE_NAME = 'parent' AND c.ORDINAL_POSITION = 1) FROM names", "parent")]
    [InlineData("SELECT name FROM names WHERE name IN (SELECT t.TABLE_NAME FROM INFORMATION_SCHEMA.TABLES t JOIN INFORMATION_SCHEMA.COLUMNS c ON t.TABLE_NAME = c.TABLE_NAME)", "parent")]
    [InlineData("SELECT name FROM names WHERE EXISTS (SELECT t.TABLE_NAME FROM INFORMATION_SCHEMA.TABLES t JOIN INFORMATION_SCHEMA.COLUMNS c ON t.TABLE_NAME = c.TABLE_NAME)", "parent")]
    [InlineData("SELECT name FROM names WHERE EXISTS (SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME IN (SELECT TABLE_NAME FROM INFORMATION_SCHEMA.COLUMNS))", "parent")]
    [InlineData("SELECT n.name FROM names n JOIN INFORMATION_SCHEMA.TABLES t ON n.name = t.TABLE_NAME WHERE EXISTS (SELECT TYPE_NAME FROM COHESION_SCHEMA.TYPES) AND n.name IN (SELECT TABLE_NAME FROM INFORMATION_SCHEMA.COLUMNS)", "parent")]
    public async Task Execute_SystemRelationPosition_ShouldCaptureOneImageAsync(string query, string expected)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("catalog-positions", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await SeedAsync(session);
        await session.ExecuteAsync("CREATE TABLE names (name TEXT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO names VALUES ('parent')", cancellationToken: CancellationToken.None);

        // Act
        var rows = await RowsAsync(session, query);

        // Assert
        Check(rows, [[expected]]);
        session.LastStatementMetrics!.CatalogSnapshotCaptures.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - System relations: INSERT SELECT consumes a joined catalog image")]
    public async Task InsertSelect_SystemJoin_ShouldCaptureOnceAndInsertExactRowsAsync()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("catalog-insert", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await SeedAsync(session);
        await session.ExecuteAsync("CREATE TABLE inventory (name TEXT, position BIGINT)", cancellationToken: CancellationToken.None);

        // Act
        var result = await session.ExecuteAsync("INSERT INTO inventory SELECT c.COLUMN_NAME, c.ORDINAL_POSITION " +
            "FROM INFORMATION_SCHEMA.TABLES t JOIN INFORMATION_SCHEMA.COLUMNS c ON t.TABLE_NAME = c.TABLE_NAME WHERE t.TABLE_NAME = 'parent'",
            cancellationToken: CancellationToken.None);

        // Assert
        result.AffectedCount.ShouldBe(2);
        session.LastStatementMetrics!.CatalogSnapshotCaptures.ShouldBe(1);
        Check(await RowsAsync(session, "SELECT * FROM inventory ORDER BY position"), [["a", 1L], ["b", 2L]]);
        session.LastStatementMetrics!.CatalogSnapshotCaptures.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - System relations: snapshot isolation retains BEGIN metadata and read committed refreshes")]
    [InlineData(IsolationLevel.Snapshot)]
    [InlineData(IsolationLevel.ReadCommitted)]
    public async Task Join_MetadataIsolation_ShouldRespectCaptureLifetimeAsync(IsolationLevel isolation)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("catalog-isolation", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var reader = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await using var writer = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await writer.ExecuteAsync("CREATE TABLE names (name TEXT)", cancellationToken: CancellationToken.None);
        await writer.ExecuteAsync("INSERT INTO names VALUES ('names'), ('newcomer'), ('later')", cancellationToken: CancellationToken.None);
        var transaction = await reader.BeginTransactionAsync(isolation, CancellationToken.None);
        await writer.ExecuteAsync("CREATE TABLE newcomer (id INT)", cancellationToken: CancellationToken.None);
        const string query = "SELECT t.TABLE_NAME FROM names n JOIN INFORMATION_SCHEMA.TABLES t ON n.name = t.TABLE_NAME ORDER BY t.TABLE_NAME";

        // Act / Assert: the first read occurs after DDL.
        Check(await RowsAsync(reader, query), isolation == IsolationLevel.Snapshot ? [["names"]] : [["names"], ["newcomer"]]);
        reader.LastStatementMetrics!.CatalogSnapshotCaptures.ShouldBe(isolation == IsolationLevel.Snapshot ? 0 : 1);
        await writer.ExecuteAsync("CREATE TABLE later (id INT)", cancellationToken: CancellationToken.None);
        Check(await RowsAsync(reader, query), isolation == IsolationLevel.Snapshot ? [["names"]] : [["later"], ["names"], ["newcomer"]]);
        reader.LastStatementMetrics!.CatalogSnapshotCaptures.ShouldBe(isolation == IsolationLevel.Snapshot ? 0 : 1);
        await transaction.RollbackAsync(CancellationToken.None);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - System relations: metadata is fixed at binding rather than context construction")]
    public async Task Plan_SystemReads_ShouldFixOneImageAtBindingAsync()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("catalog-binding", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var writer = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        var transaction = await database.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, CancellationToken.None);
        try
        {
            var context = new SqlStatementContext(transaction, database.Coordinator, databaseName: "app");
            context.CatalogSnapshot.ShouldBeNull();
            await writer.ExecuteAsync("CREATE TABLE before_bind (id INT)", cancellationToken: CancellationToken.None);
            var query = SqlQueryRequest.FromSql("SELECT t.TABLE_NAME, c.COLUMN_NAME FROM INFORMATION_SCHEMA.TABLES t " +
                "JOIN INFORMATION_SCHEMA.COLUMNS c ON t.TABLE_NAME = c.TABLE_NAME");

            // Act
            var plan = new SqlPlanner(database.Catalog, null, database.Definitions.Functions, statement: context).Plan(query.Statement.SqlExpression);
            await writer.ExecuteAsync("CREATE TABLE after_bind (id INT)", cancellationToken: CancellationToken.None);
            await using var result = (await new SqlPlanExecutor(database.DataStorage, database.Catalog, database.IndexManager, database.Definitions)
                .ExecuteAsync(plan, context, CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();

            // Assert
            Check(await ReadAsync(result), [["before_bind", "id"]]);
            context.Metrics.CatalogSnapshotCaptures.ShouldBe(1);
        }
        finally { await database.Coordinator.RollbackAsync(transaction, CancellationToken.None); }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - System relations: missing catalog capture fails as an internal invariant")]
    [InlineData("SELECT * FROM INFORMATION_SCHEMA.TABLES")]
    [InlineData("SELECT * FROM COHESION_SCHEMA.FUNCTIONS")]
    [InlineData("SELECT * FROM COHESION_SCHEMA.TYPES")]
    [InlineData("SELECT * FROM COHESION_SCHEMA.DOMAINS")]
    [InlineData("SELECT * FROM INFORMATION_SCHEMA.TABLES t JOIN INFORMATION_SCHEMA.COLUMNS c ON t.TABLE_NAME = c.TABLE_NAME")]
    public async Task Execute_UncapturedSystemPlan_ShouldThrowInvariantAsync(string query)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("catalog-invariant", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        var transaction = await database.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, CancellationToken.None);
        try
        {
            var plan = new SqlPlanner(database.Catalog, null).Plan(SqlQueryRequest.FromSql(query).Statement.SqlExpression);
            var executor = new SqlPlanExecutor(database.DataStorage, database.Catalog, database.IndexManager, database.Definitions);

            // Act
            var error = await Should.ThrowAsync<DatabaseException>(async () => await executor.ExecuteAsync(plan,
                new SqlStatementContext(transaction, database.Coordinator), CancellationToken.None));

            // Assert
            error.Message.ShouldContain("Internal invariant");
        }
        finally { await database.Coordinator.RollbackAsync(transaction, CancellationToken.None); }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - System relations: catalog joins acquire no stored-object locks")]
    public async Task Join_SystemOnly_ShouldLeaveStoredObjectsUnlockedAsync()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("catalog-locks", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await SeedAsync(session);
        var reader = await session.BeginTransactionAsync(IsolationLevel.ReadCommitted, CancellationToken.None);
        var blocker = await database.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, CancellationToken.None);
        try
        {
            // Act
            var rows = await RowsAsync(session, "SELECT t.TABLE_NAME FROM INFORMATION_SCHEMA.TABLES t " +
                "JOIN INFORMATION_SCHEMA.COLUMNS c ON t.TABLE_NAME = c.TABLE_NAME");

            // Assert: another owner can still acquire exclusive locks while this transaction remains active.
            rows.Count.ShouldBe(7);
            session.LastStatementMetrics!.JoinAccessPaths.ShouldBe(["join-hash"]);
            foreach (string name in new[] { "parent", "child" })
            {
                database.Catalog.TryGetTable("dbo", name, out var table).ShouldBeTrue();
                database.Coordinator.LockManager.TryAcquire(blocker.Sequence,
                    LockResource.Object(table!.ObjectId), LockMode.Exclusive).ShouldBeTrue();
            }
        }
        finally
        {
            await database.Coordinator.RollbackAsync(blocker, CancellationToken.None);
            await reader.RollbackAsync(CancellationToken.None);
        }
    }

    private static async Task SeedAsync(SqlDatabaseSession session)
    {
        await session.ExecuteAsync("CREATE VIEW seeded_view AS SELECT SCHEMA_NAME FROM INFORMATION_SCHEMA.SCHEMATA", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE parent (a INT, b INT, CONSTRAINT pk_parent PRIMARY KEY(b,a))", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE child (id INT, parent_a INT, parent_b INT, quantity INT, CONSTRAINT pk_child PRIMARY KEY(id), " +
            "CONSTRAINT fk_parent FOREIGN KEY(parent_b,parent_a) REFERENCES parent(b,a), CONSTRAINT ck_quantity CHECK(quantity > 0))", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE DOMAIN positive AS INT CHECK (VALUE > 0)", cancellationToken: CancellationToken.None);
    }

    private static void Check(IReadOnlyList<object?[]> actual, IReadOnlyList<object?[]> expected)
    {
        actual.Count.ShouldBe(expected.Count);
        for (int i = 0; i < expected.Count; i++) { actual[i].ShouldBe(expected[i]); }
    }

    private static async Task<List<object?[]>> RowsAsync(SqlDatabaseSession session, string query)
    {
        await using var result = (await session.ExecuteAsync(query, cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        return await ReadAsync(result);
    }

    private static async Task<List<object?[]>> ReadAsync(DatabaseResultSet result)
    {
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        { rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray()); }
        return rows;
    }
}
