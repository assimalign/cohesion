using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Types;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>Exercises collation agreement across index writes, seeks, constraints and restart.</summary>
public sealed class SqlCollationIndexTests
{
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Collation: folded seeks and ranges preserve original row text")]
    public async Task Seek_FoldedColumns_ShouldAgreeWithScanAndExpressionOverrides()
    {
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("collation-seeks", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await Execute(session, "CREATE TABLE t (id INT, name TEXT COLLATE case_accent_insensitive)");
        await Execute(session, "INSERT INTO t VALUES (1, 'Álice'), (2, 'alice'), (3, 'Bob'), (4, 'charlie')");
        string[] predicates = ["name = 'ALICE'", "name >= 'ALICE' AND name < 'CHARLIE'", "name BETWEEN 'alice' AND 'BOB'", "name COLLATE case_accent_insensitive = 'alice'"];
        var expected = new List<string[]>();
        foreach (string predicate in predicates)
        {
            expected.Add(await Names(session, $"SELECT name FROM t WHERE {predicate} ORDER BY id"));
        }
        await Execute(session, "CREATE INDEX ix_name ON t(name)");
        for (int i = 0; i < predicates.Length; i++)
        {
            (await Names(session, $"SELECT name FROM t WHERE {predicates[i]} ORDER BY id")).ShouldBe(expected[i]);
            Metrics(session).AccessPath.ShouldBe("seek:ix_name");
        }
        expected[0].ShouldBe(new[] { "Álice", "alice" });
        (await Names(session, "SELECT name FROM t WHERE name = 'alice' COLLATE binary")).ShouldBe(new[] { "alice" });
        Metrics(session).AccessPath.ShouldBe("scan");
        (await Names(session, "SELECT name FROM t WHERE name COLLATE binary = 'alice'")).ShouldBe(new[] { "alice" });
        Metrics(session).AccessPath.ShouldBe("scan");
        (await Names(session, "SELECT name FROM t WHERE name = 'alice' COLLATE invariant")).ShouldBe(new[] { "alice" });
        Metrics(session).AccessPath.ShouldBe("scan");
        await Execute(session, "CREATE TABLE exact (name TEXT COLLATE binary)");
        await Execute(session, "INSERT INTO exact VALUES ('Alice'), ('alice')");
        await Execute(session, "CREATE INDEX ix_exact ON exact(name)");
        (await Names(session, "SELECT name FROM exact WHERE 'alice' COLLATE case_insensitive = name COLLATE binary ORDER BY name"))
            .ShouldBe(new[] { "Alice", "alice" });
        Metrics(session).AccessPath.ShouldBe("scan");
        (await Names(session, "SELECT name FROM exact WHERE 'alice' COLLATE binary = name COLLATE case_insensitive"))
            .ShouldBe(new[] { "alice" });
        Metrics(session).AccessPath.ShouldBe("seek:ix_exact");
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Collation: UNIQUE index rejects folded duplicates on insert and update")]
    public async Task Unique_FoldedKeys_ShouldRejectInsertUpdateAndBackfillDuplicates()
    {
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("collation-unique", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await Execute(session, "CREATE TABLE t (id INT, name TEXT COLLATE case_insensitive UNIQUE)");
        await Execute(session, "INSERT INTO t VALUES (1, 'Alice'), (2, 'Bob')");
        await Should.ThrowAsync<SqlConstraintViolationException>(() => Execute(session, "INSERT INTO t VALUES (3, 'alice')"));
        await Should.ThrowAsync<SqlConstraintViolationException>(() => Execute(session, "UPDATE t SET name = 'ALICE' WHERE id = 2"));
        (await Names(session, "SELECT name FROM t ORDER BY id")).ShouldBe(new[] { "Alice", "Bob" });
        await Execute(session, "UPDATE t SET name = 'ALICE' WHERE id = 1");
        (await Names(session, "SELECT name FROM t WHERE name = 'alice'")).ShouldBe(new[] { "ALICE" });
        await Execute(session, "CREATE TABLE duplicates (name TEXT COLLATE case_insensitive)");
        await Execute(session, "INSERT INTO duplicates VALUES ('Alice'), ('alice')");
        await Should.ThrowAsync<SqlConstraintViolationException>(() => Execute(session, "CREATE UNIQUE INDEX ux ON duplicates(name)"));
        await Should.ThrowAsync<SqlConstraintViolationException>(() => Execute(session, "ALTER TABLE duplicates ADD CONSTRAINT uq UNIQUE(name)"));
        await Execute(session, "CREATE TABLE composite (tenant INT, name TEXT COLLATE case_insensitive, UNIQUE(tenant, name))");
        await Execute(session, "INSERT INTO composite VALUES (1, 'Alice'), (2, 'alice')");
        await Should.ThrowAsync<SqlConstraintViolationException>(() => Execute(session, "INSERT INTO composite VALUES (1, 'ALICE')"));
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Collation: default and column overrides survive restart with enforcing indexes")]
    public async Task Restart_DefaultAndColumnCollations_ShouldKeepSeekAndUniqueSemantics()
    {
        string root = Path.Combine(Path.GetTempPath(), "cohesion-collation", Guid.NewGuid().ToString("N"));
        try
        {
            await using (var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { RootPath = root }))
            {
                var database = await engine.CreateDatabaseAsync("restart", Collation.CaseInsensitive, CancellationToken.None);
                await using var session = await database.CreateSessionAsync(CancellationToken.None);
                await Execute(session, "CREATE TABLE t (name TEXT UNIQUE, exact TEXT COLLATE binary, accent TEXT COLLATE case_accent_insensitive)");
                await Execute(session, "INSERT INTO t VALUES ('Alice', 'Alice', 'Élodie')");
                await Execute(session, "CREATE INDEX ix_accent ON t(accent)");
            }
            await using (var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { RootPath = root }))
            {
                var database = await engine.OpenDatabaseAsync("restart", CancellationToken.None);
                var catalog = ((SqlDatabaseInstance)database).Catalog;
                catalog.DefaultCollation.ShouldBeSameAs(Collation.CaseInsensitive);
                catalog.TryGetTable("dbo", "t", out var table).ShouldBeTrue();
                table.Columns[0].Collation.ShouldBeNull();
                table.Columns[1].Collation.ShouldBeSameAs(Collation.Binary);
                table.Columns[2].Collation.ShouldBeSameAs(Collation.CaseAccentInsensitive);
                await using var session = await database.CreateSessionAsync(CancellationToken.None);
                (await Names(session, "SELECT name FROM t WHERE name = 'alice'")).ShouldBe(new[] { "Alice" });
                Metrics(session).AccessPath.ShouldStartWith("seek:");
                (await Names(session, "SELECT name FROM t WHERE exact = 'alice'")).ShouldBeEmpty();
                (await Names(session, "SELECT name FROM t WHERE accent = 'ELODIE'")).ShouldBe(new[] { "Alice" });
                Metrics(session).AccessPath.ShouldBe("seek:ix_accent");
                await Should.ThrowAsync<SqlConstraintViolationException>(() => Execute(session, "INSERT INTO t VALUES ('alice', 'other', 'other')"));
                await Execute(session, "CREATE TABLE inherited (name TEXT)");
                await Execute(session, "INSERT INTO inherited VALUES ('Bob')");
                (await Names(session, "SELECT name FROM inherited WHERE name = 'bob'")).ShouldBe(new[] { "Bob" });
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Collation: binary default stays isolated from a folded database")]
    public async Task Database_Defaults_ShouldRemainIsolatedAndBinaryWhenUnspecified()
    {
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions());
        foreach (bool folded in new[] { true, false })
        {
            var database = folded
                ? await engine.CreateDatabaseAsync("folded", Collation.CaseInsensitive, CancellationToken.None)
                : await engine.CreateDatabaseAsync("binary", CancellationToken.None);
            await using var session = await database.CreateSessionAsync(CancellationToken.None);
            await Execute(session, "CREATE TABLE t (name TEXT UNIQUE)");
            await Execute(session, "INSERT INTO t VALUES ('Alice')");
            (await Names(session, "SELECT name FROM t WHERE name = 'alice'")).Length.ShouldBe(folded ? 1 : 0);
            (await Names(session, "SELECT name FROM t WHERE 'Alice' = 'alice'")).Length.ShouldBe(folded ? 1 : 0);
            if (!folded)
            {
                await Execute(session, "INSERT INTO t VALUES ('alice')");
                (await Names(session, "SELECT name FROM t GROUP BY name ORDER BY name")).Length.ShouldBe(2);
            }
        }
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Collation: linguistic indexes and non-string column overrides reject clearly")]
    public async Task Index_NonByteCollation_ShouldRejectBeforePublishingEmptyIndex()
    {
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("unsupported", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await Execute(session, "CREATE TABLE t (name TEXT COLLATE invariant)");
        (await Should.ThrowAsync<DatabaseException>(() => Execute(session, "CREATE INDEX ix ON t(name)"))).Message.ShouldContain("not index-backed");
        (await Should.ThrowAsync<DatabaseException>(() => Execute(session, "CREATE TABLE bad (name TEXT COLLATE invariant UNIQUE)"))).Message.ShouldContain("not index-backed");
        await Should.ThrowAsync<DatabaseException>(() => Execute(session, "CREATE TABLE bad_number (id INT COLLATE binary)"));
        ((SqlDatabaseInstance)database).Catalog.TryGetTable("dbo", "bad", out _).ShouldBeFalse();
        await Execute(session, "INSERT INTO t VALUES ('Alice')");
        (await Names(session, "SELECT name FROM t WHERE name = 'Alice'")).ShouldBe(new[] { "Alice" });
    }

    private static async Task Execute(IDatabaseSession session, string sql)
        => await session.ExecuteAsync(SqlQueryRequest.FromSql(sql), CancellationToken.None);

    private static SqlStatementMetrics Metrics(IDatabaseSession session)
        => ((SqlDatabaseSession)session).LastStatementMetrics.ShouldNotBeNull();

    private static async Task<string[]> Names(IDatabaseSession session, string sql)
    {
        await using var result = (QueryResultSet)await session.ExecuteAsync(SqlQueryRequest.FromSql(sql), CancellationToken.None);
        var values = new List<string>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        {
            values.Add((string)row.GetValue(0)!);
        }
        return values.ToArray();
    }
}
