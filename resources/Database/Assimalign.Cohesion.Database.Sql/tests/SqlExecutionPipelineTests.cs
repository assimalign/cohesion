using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Transactions;

/// <summary>
/// End-to-end tests for the SQL engine (#178/#179): sessions and transactions over
/// real SQL — DDL through the catalog, DML through the planner and plan executor,
/// deterministic SELECT results against shared storage.
/// </summary>
public class SqlExecutionPipelineTests : IDisposable
{
    private readonly string _rootPath;

    public SqlExecutionPipelineTests()
    {
        _rootPath = Path.Combine(Path.GetTempPath(), "cohesion-sql-tests", Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            try
            {
                Directory.Delete(_rootPath, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup
            }
        }
    }

    private async Task<SqlDatabaseEngine> CreateEngine()
    {
        var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions
        {
            EngineName = "test-engine",
            RootPath = _rootPath
        });
        return engine;
    }

    private static Task<QueryResult> Sql(IDatabaseSession session, string sql, IReadOnlyDictionary<string, object?>? parameters = null)
        => session.ExecuteAsync(SqlQueryRequest.FromSql(sql, parameters)).AsTask();

    private static async Task<List<object?[]>> Rows(IDatabaseSession session, string sql, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        var result = await Sql(session, sql, parameters);
        var resultSet = result.ShouldBeAssignableTo<QueryResultSet>();

        var rows = new List<object?[]>();
        await foreach (var row in resultSet!.GetRowsAsync())
        {
            var values = new object?[row.FieldCount];
            for (int i = 0; i < row.FieldCount; i++)
            {
                values[i] = row.GetValue(i);
            }
            rows.Add(values);
        }

        return rows;
    }

    private static async Task<IDatabaseSession> OpenSeededSessionAsync(IDatabase database)
    {
        var session = await database.CreateSessionAsync();
        await Sql(session, "CREATE TABLE users (id BIGINT PRIMARY KEY, name VARCHAR(100), age INT);");
        await Sql(session, "INSERT INTO users (id, name, age) VALUES (1, 'Ada', 36), (2, 'Grace', 45), (3, 'Alan', 41);");
        return session;
    }

    // ── Session lifecycle ──────────────────────────────────────────────

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - CreateSession: Should return open session")]
    public async Task CreateSession_ShouldReturnOpenSession()
    {
        await using var engine = await CreateEngine();
        var db = await engine.CreateDatabaseAsync("test-db");

        await using var session = await db.CreateSessionAsync();

        session.State.ShouldBe(SessionState.Open);
        session.Database.ShouldBe(db);
        session.CurrentTransaction.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - DisposeSession: Should transition to closed and be idempotent")]
    public async Task DisposeSession_ShouldCloseIdempotently()
    {
        await using var engine = await CreateEngine();
        var db = await engine.CreateDatabaseAsync("test-db");
        var session = await db.CreateSessionAsync();

        await session.DisposeAsync();
        await session.DisposeAsync();

        session.State.ShouldBe(SessionState.Closed);
        await Should.ThrowAsync<DatabaseException>(async () => await Sql(session, "SELECT * FROM users;"));
    }

    // ── Transactions ───────────────────────────────────────────────────

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - BeginTransaction: lifecycle transitions and guards")]
    public async Task BeginTransaction_Lifecycle_ShouldTransitionAndGuard()
    {
        await using var engine = await CreateEngine();
        var db = await engine.CreateDatabaseAsync("test-db");
        await using var session = await db.CreateSessionAsync();

        var transaction = await session.BeginTransactionAsync();
        transaction.State.ShouldBe(TransactionState.Active);
        transaction.Id.ShouldNotBe(default);

        await Should.ThrowAsync<DatabaseException>(async () => await session.BeginTransactionAsync());

        await transaction.CommitAsync();
        transaction.State.ShouldBe(TransactionState.Committed);
        await Should.ThrowAsync<DatabaseException>(async () => await transaction.CommitAsync());
        await Should.ThrowAsync<DatabaseException>(async () => await transaction.RollbackAsync());
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Rollback: explicit rollback undoes inserted rows")]
    public async Task Rollback_ExplicitTransaction_ShouldUndoRows()
    {
        await using var engine = await CreateEngine();
        var db = await engine.CreateDatabaseAsync("test-db");
        var session = await OpenSeededSessionAsync(db);
        await using var _ = session;

        var transaction = await session.BeginTransactionAsync();
        await Sql(session, "INSERT INTO users (id, name, age) VALUES (99, 'Ghost', 1);");
        await transaction.RollbackAsync();

        (await Rows(session, "SELECT id FROM users;")).Count.ShouldBe(3);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Dispose: active transaction rolls back with its session")]
    public async Task DisposeSession_ActiveTransaction_ShouldRollBack()
    {
        await using var engine = await CreateEngine();
        var db = await engine.CreateDatabaseAsync("test-db");

        var session = await OpenSeededSessionAsync(db);
        var transaction = await session.BeginTransactionAsync();
        await Sql(session, "DELETE FROM users;");
        await session.DisposeAsync();

        transaction.State.ShouldBe(TransactionState.RolledBack);

        await using var verify = await db.CreateSessionAsync();
        (await Rows(verify, "SELECT id FROM users;")).Count.ShouldBe(3);
    }

    // ── DDL ────────────────────────────────────────────────────────────

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - DDL: create, alter, and drop flow through the catalog")]
    public async Task Ddl_CreateAlterDrop_ShouldFlowThroughCatalog()
    {
        await using var engine = await CreateEngine();
        var db = await engine.CreateDatabaseAsync("test-db");
        await using var session = await db.CreateSessionAsync();

        await Sql(session, "CREATE TABLE t (id INT NOT NULL PRIMARY KEY, note TEXT);");
        await Sql(session, "CREATE TABLE IF NOT EXISTS t (id INT);"); // no-op, no throw
        await Should.ThrowAsync<DatabaseException>(async () => await Sql(session, "CREATE TABLE t (id INT);"));

        await Sql(session, "ALTER TABLE t ADD COLUMN extra BIGINT;");
        await Sql(session, "INSERT INTO t (id, note, extra) VALUES (1, 'x', 5);");
        await Sql(session, "ALTER TABLE t DROP COLUMN note;");

        var rows = await Rows(session, "SELECT id, extra FROM t;");
        rows.Count.ShouldBe(1);
        rows[0][1].ShouldBe(5L);

        await Sql(session, "DROP TABLE t;");
        await Should.ThrowAsync<DatabaseException>(async () => await Sql(session, "SELECT * FROM t;"));
        await Sql(session, "DROP TABLE IF EXISTS t;"); // no-op
    }

    // ── DML + SELECT ───────────────────────────────────────────────────

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Insert: affected counts and defaults apply")]
    public async Task Insert_WithDefaultsAndCounts_ShouldApply()
    {
        await using var engine = await CreateEngine();
        var db = await engine.CreateDatabaseAsync("test-db");
        await using var session = await db.CreateSessionAsync();

        await Sql(session, "CREATE TABLE items (id INT NOT NULL, label VARCHAR(50) DEFAULT 'unlabeled', qty INT DEFAULT 0);");

        var result = await Sql(session, "INSERT INTO items (id) VALUES (1), (2);");
        result.AffectedCount.ShouldBe(2);

        var rows = await Rows(session, "SELECT id, label, qty FROM items ORDER BY id;");
        rows[0].ShouldBe(new object?[] { 1, "unlabeled", 0 });
        rows[1][0].ShouldBe(2);

        // NOT NULL without a default fails loudly.
        await Should.ThrowAsync<DatabaseException>(async () => await Sql(session, "INSERT INTO items (label) VALUES ('x');"));
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Select: WHERE, projection, ORDER BY, LIMIT/OFFSET are deterministic")]
    public async Task Select_FilterProjectSortLimit_ShouldBeDeterministic()
    {
        await using var engine = await CreateEngine();
        var db = await engine.CreateDatabaseAsync("test-db");
        var session = await OpenSeededSessionAsync(db);
        await using var _ = session;

        var filtered = await Rows(session, "SELECT name FROM users WHERE age > 40 ORDER BY age DESC;");
        filtered.Count.ShouldBe(2);
        filtered[0][0].ShouldBe("Grace");
        filtered[1][0].ShouldBe("Alan");

        var window = await Rows(session, "SELECT id FROM users ORDER BY id LIMIT 1 OFFSET 1;");
        window.Count.ShouldBe(1);
        window[0][0].ShouldBe(2L);

        var computed = await Rows(session, "SELECT name, age + 1 AS next_age FROM users WHERE name LIKE 'A%' ORDER BY name;");
        computed.Count.ShouldBe(2);
        computed[0][0].ShouldBe("Ada");
        computed[0][1].ShouldBe(37L);

        (await Rows(session, "SELECT COUNT(*) FROM users;"))[0][0].ShouldBe(3L);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Update/Delete: WHERE-scoped with accurate counts")]
    public async Task UpdateDelete_WithWhere_ShouldScopeAndCount()
    {
        await using var engine = await CreateEngine();
        var db = await engine.CreateDatabaseAsync("test-db");
        var session = await OpenSeededSessionAsync(db);
        await using var _ = session;

        var updated = await Sql(session, "UPDATE users SET age = age + 10 WHERE name = 'Ada';");
        updated.AffectedCount.ShouldBe(1);
        (await Rows(session, "SELECT age FROM users WHERE id = 1;"))[0][0].ShouldBe(46);

        var deleted = await Sql(session, "DELETE FROM users WHERE age < 45;");
        deleted.AffectedCount.ShouldBe(1); // only Alan (41); Ada is 46 now, Grace 45
        (await Rows(session, "SELECT id FROM users;")).Count.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Parameters: named parameters bind into predicates and values")]
    public async Task Parameters_NamedValues_ShouldBind()
    {
        await using var engine = await CreateEngine();
        var db = await engine.CreateDatabaseAsync("test-db");
        var session = await OpenSeededSessionAsync(db);
        await using var _ = session;

        await Sql(session, "INSERT INTO users (id, name, age) VALUES (@id, @name, @age);", new Dictionary<string, object?>
        {
            ["id"] = 10L,
            ["name"] = "Barbara",
            ["age"] = 33,
        });

        var rows = await Rows(session, "SELECT name FROM users WHERE age = @age;", new Dictionary<string, object?>
        {
            ["age"] = 33,
        });

        rows.Count.ShouldBe(1);
        rows[0][0].ShouldBe("Barbara");

        await Should.ThrowAsync<DatabaseException>(async () =>
            await Rows(session, "SELECT name FROM users WHERE age = @missing;"));
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Select: DISTINCT and NULL semantics behave as SQL")]
    public async Task Select_DistinctAndNulls_ShouldFollowSqlSemantics()
    {
        await using var engine = await CreateEngine();
        var db = await engine.CreateDatabaseAsync("test-db");
        await using var session = await db.CreateSessionAsync();

        await Sql(session, "CREATE TABLE t (category VARCHAR(10), score INT);");
        await Sql(session, "INSERT INTO t (category, score) VALUES ('a', 1), ('a', 1), ('b', NULL), ('b', 2);");

        (await Rows(session, "SELECT DISTINCT category FROM t;")).Count.ShouldBe(2);

        // NULL never matches a comparison; IS NULL does.
        (await Rows(session, "SELECT category FROM t WHERE score = 1;")).Count.ShouldBe(2);
        (await Rows(session, "SELECT category FROM t WHERE score IS NULL;")).Count.ShouldBe(1);
        (await Rows(session, "SELECT category FROM t WHERE score <> 999;")).Count.ShouldBe(3); // the NULL row drops out
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Types: typed round-trips through storage")]
    public async Task Insert_TypedColumns_ShouldRoundTrip()
    {
        await using var engine = await CreateEngine();
        var db = await engine.CreateDatabaseAsync("test-db");
        await using var session = await db.CreateSessionAsync();

        await Sql(session, "CREATE TABLE typed (flag BOOLEAN, amount DECIMAL(18, 4), ratio FLOAT, uid UUID, label TEXT);");

        var guid = Guid.NewGuid();
        await Sql(session, "INSERT INTO typed (flag, amount, ratio, uid, label) VALUES (TRUE, 1234.5678, 2.5, @uid, 'it''s');", new Dictionary<string, object?>
        {
            ["uid"] = guid,
        });

        var rows = await Rows(session, "SELECT flag, amount, ratio, uid, label FROM typed;");
        rows[0][0].ShouldBe(true);
        rows[0][1].ShouldBe(1234.5678m);
        rows[0][2].ShouldBe(2.5);
        rows[0][3].ShouldBe(guid);
        rows[0][4].ShouldBe("it's");
    }

    // ── Unsupported-feature diagnostics ────────────────────────────────

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Planner: unsupported features fail with precise messages")]
    public async Task Planner_UnsupportedFeatures_ShouldFailPrecisely()
    {
        await using var engine = await CreateEngine();
        var db = await engine.CreateDatabaseAsync("test-db");
        var session = await OpenSeededSessionAsync(db);
        await using var _ = session;

        (await Should.ThrowAsync<DatabaseException>(async () =>
            await Sql(session, "SELECT * FROM users u INNER JOIN users v ON u.id = v.id;")))
            .Message.ShouldContain("JOIN", Case.Sensitive);

        (await Should.ThrowAsync<DatabaseException>(async () =>
            await Sql(session, "SELECT age FROM users GROUP BY age;")))
            .Message.ShouldContain("GROUP BY", Case.Sensitive);

        (await Should.ThrowAsync<DatabaseException>(async () =>
            await Sql(session, "SELECT SUM(age) FROM users;")))
            .Message.ShouldContain("Aggregate", Case.Sensitive);

        (await Should.ThrowAsync<DatabaseException>(async () =>
            await Sql(session, "SELECT * FROM missing_table;")))
            .Message.ShouldContain("does not exist", Case.Sensitive);
    }

    // ── Engine lifecycle ───────────────────────────────────────────────

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Engine: database registry lifecycle")]
    public async Task Engine_DatabaseRegistry_ShouldEnforceLifecycle()
    {
        await using var engine = await CreateEngine();

        var db = await engine.CreateDatabaseAsync("db-one");
        await Should.ThrowAsync<DatabaseException>(async () => await engine.CreateDatabaseAsync("db-one"));

        engine.TryGetDatabase("db-one", out var found).ShouldBeTrue();
        found.ShouldBe(db);

        await engine.DropDatabaseAsync("db-one");
        engine.TryGetDatabase("db-one", out _).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Engine: in-memory strategy works without a root path")]
    public async Task Engine_InMemoryStrategy_ShouldExecuteSql()
    {
        var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "memory" });
        await using var _ = engine;

        var db = await engine.CreateDatabaseAsync("mem-db");
        await using var session = await db.CreateSessionAsync();

        await Sql(session, "CREATE TABLE t (id INT);");
        await Sql(session, "INSERT INTO t (id) VALUES (7);");
        (await Rows(session, "SELECT id FROM t;"))[0][0].ShouldBe(7);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Session: the text-execute seam parses SQL and binds parameters")]
    public async Task ExecuteAsync_StatementText_ShouldParseAndBind()
    {
        // Arrange: the model-agnostic seam the wire-protocol server calls — the
        // SQL session owns translating text into a typed request.
        var engine = await CreateEngine();
        await using var _ = engine;

        var db = await engine.CreateDatabaseAsync("text-seam");
        await using var session = await db.CreateSessionAsync();

        // Act
        await session.ExecuteAsync("CREATE TABLE t (id INT NOT NULL, label VARCHAR(50));");
        var insert = await session.ExecuteAsync(
            "INSERT INTO t (id, label) VALUES (@id, @label);",
            new Dictionary<string, object?> { ["id"] = 5, ["label"] = "five" });

        var result = await session.ExecuteAsync("SELECT label FROM t WHERE id = @id;", new Dictionary<string, object?> { ["id"] = 5 });

        // Assert
        insert.AffectedCount.ShouldBe(1);
        var resultSet = result.ShouldBeAssignableTo<QueryResultSet>();
        await foreach (var row in resultSet!.GetRowsAsync())
        {
            row.GetString(0).ShouldBe("five");
        }

        // Parse failures surface as the dedicated parse-exception category.
        await Should.ThrowAsync<DatabaseParseException>(async () => await session.ExecuteAsync("TRUNCATE TABLE t;"));
    }
}
