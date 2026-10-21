using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlProgramLimitReviewTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - ADD CONSTRAINT: oversized existing keys leave no constraint or index (#1266)")]
    [InlineData("UNIQUE")]
    [InlineData("PRIMARY KEY")]
    public async Task ExecuteAsync_AddConstraintOverOversizedKey_ShouldRefuseAndPermitRetry(string kind)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("constraint-size", new());
        var database = await engine.CreateDatabaseAsync("app", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await session.ExecuteAsync("CREATE TABLE t (v TEXT NOT NULL)", cancellationToken: timeout.Token);
        await session.ExecuteAsync("INSERT INTO t VALUES (@v)", Parameters(new string('x', 2000)), timeout.Token);
        database.Catalog.TryGetTable("dbo", "t", out var table).ShouldBeTrue();

        // Act
        var failure = await Should.ThrowAsync<SqlEvaluationException>(async () =>
            await session.ExecuteAsync($"ALTER TABLE t ADD CONSTRAINT k {kind} (v)", cancellationToken: timeout.Token));

        // Assert
        failure.Code.ShouldBe("COHSQLE042");
        database.Catalog.TryGetTable("dbo", "t", out table).ShouldBeTrue();
        table.Constraints.ShouldBeEmpty();
        database.Catalog.GetIndexes(table.ObjectId).ShouldBeEmpty();
        (await ReadAsync(session, "SELECT v FROM t", timeout.Token)).ShouldHaveSingleItem()[0].ShouldBe(new string('x', 2000));
        // Historical versions are indexed too. Reuse the name on a different, fitting key.
        await session.ExecuteAsync("ALTER TABLE t ADD COLUMN small TEXT DEFAULT 'ok' NOT NULL", cancellationToken: timeout.Token);
        await session.ExecuteAsync($"ALTER TABLE t ADD CONSTRAINT k {kind} (small)", cancellationToken: timeout.Token);
        database.Catalog.GetIndexes(table.ObjectId).ShouldHaveSingleItem();
        await Should.ThrowAsync<SqlConstraintViolationException>(async () =>
            await session.ExecuteAsync("INSERT INTO t (v) VALUES ('second')", cancellationToken: timeout.Token));
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - ADD COLUMN UNIQUE: oversized DEFAULT leaves the old schema intact (#1266)")]
    public async Task ExecuteAsync_AddUniqueColumnWithOversizedDefault_ShouldRefuseAndPermitRetry()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("column-size", new());
        var database = await engine.CreateDatabaseAsync("app", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await session.ExecuteAsync("CREATE TABLE t (id INT)", cancellationToken: timeout.Token);
        await session.ExecuteAsync("INSERT INTO t VALUES (1)", cancellationToken: timeout.Token);
        database.Catalog.TryGetTable("dbo", "t", out var table).ShouldBeTrue();

        // Act
        var failure = await Should.ThrowAsync<SqlEvaluationException>(async () =>
            await session.ExecuteAsync($"ALTER TABLE t ADD COLUMN v TEXT DEFAULT '{new string('x', 2000)}' UNIQUE", cancellationToken: timeout.Token));

        // Assert
        failure.Code.ShouldBe("COHSQLE042");
        database.Catalog.TryGetTable("dbo", "t", out table).ShouldBeTrue();
        table.Columns.Select(column => column.Name).ShouldBe(["id"]);
        table.Constraints.ShouldBeEmpty();
        database.Catalog.GetIndexes(table.ObjectId).ShouldBeEmpty();
        await session.ExecuteAsync("ALTER TABLE t ADD COLUMN v TEXT DEFAULT 'ok' UNIQUE", cancellationToken: timeout.Token);
        (await ReadAsync(session, "SELECT id, v FROM t", timeout.Token)).ShouldHaveSingleItem().ShouldBe(new object?[] { 1, "ok" });
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Index keys: composite NULL components and folded collation use encoded bytes (#1266)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_CompositeOrFoldedKey_ShouldMeasureEncodedWriteBytes(bool folded)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("encoded-key-size", new());
        var database = await engine.CreateDatabaseAsync("app", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        string first = new('a', 600);
        await session.ExecuteAsync(folded ? "CREATE TABLE t (v TEXT COLLATE case_accent_insensitive UNIQUE)"
            : "CREATE TABLE t (a TEXT, v TEXT, c INT, UNIQUE(a, v, c))", cancellationToken: timeout.Token);
        var writer = new DatabaseKeyWriter();
        if (!folded) { SqlRowCodec.AppendKeyValue(writer, DatabaseType.String, first, Collation.Binary); }
        SqlRowCodec.AppendKeyValue(writer, DatabaseType.String, "", folded ? Collation.CaseAccentInsensitive : Collation.Binary);
        if (!folded) { SqlRowCodec.AppendKeyValue(writer, DatabaseType.Int32, null, Collation.Binary); }
        int length = BTreeIndex.MaxKeyLength - IndexKey.From(writer).Length;
        // Accent folding reduces each two-byte source character to one encoded byte.
        string value = new(folded ? 'é' : 'x', length);
        string sql = folded ? "INSERT INTO t VALUES (@v)" : $"INSERT INTO t VALUES ('{first}', @v, NULL)";

        // Act
        await session.ExecuteAsync(sql, Parameters(value), timeout.Token);
        var failure = await Should.ThrowAsync<SqlEvaluationException>(async () =>
            await session.ExecuteAsync(sql, Parameters(value + (folded ? "é" : "x")), timeout.Token));

        // Assert
        failure.Code.ShouldBe("COHSQLE042");
        failure.Message.ShouldContain("size 1025 bytes, maximum size 1024 bytes", Case.Sensitive);
        (await ReadAsync(session, "SELECT v FROM t", timeout.Token)).ShouldHaveSingleItem()[0].ShouldBe(value);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - ON DELETE SET NULL: oversized materialized rows preserve parent and child (#1266)")]
    public async Task ExecuteAsync_SetNullWithOversizedReplacement_ShouldRefuseBeforeDeletingParent()
    {
        // Arrange: a missing-tail default makes the logical child larger than its stored record.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("set-null-size", new());
        var database = await engine.CreateDatabaseAsync("app", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await session.ExecuteAsync("CREATE TABLE p (id INT PRIMARY KEY)", cancellationToken: timeout.Token);
        await session.ExecuteAsync("CREATE TABLE c (id INT PRIMARY KEY, pid INT REFERENCES p(id) ON DELETE SET NULL, v TEXT)", cancellationToken: timeout.Token);
        await session.ExecuteAsync("INSERT INTO p VALUES (1)", cancellationToken: timeout.Token);
        await session.ExecuteAsync("INSERT INTO c VALUES (1, 1, @v)", Parameters(new string('x', 7300)), timeout.Token);
        await session.ExecuteAsync($"ALTER TABLE c ADD COLUMN tail TEXT DEFAULT '{new string('y', 900)}'", cancellationToken: timeout.Token);
        await using var transaction = await session.BeginTransactionAsync(timeout.Token);

        // Act
        var failure = await Should.ThrowAsync<SqlEvaluationException>(async () =>
            await session.ExecuteAsync("DELETE FROM p WHERE id = 1", cancellationToken: timeout.Token));

        // Assert: the old logical row remains readable and can be updated to a fitting row.
        failure.Code.ShouldBe("COHSQLE042");
        transaction.State.ShouldBe(TransactionState.Active);
        (await ReadAsync(session, "SELECT id FROM p", timeout.Token)).ShouldHaveSingleItem()[0].ShouldBe(1);
        (await ReadAsync(session, "SELECT pid, LENGTH(v), LENGTH(tail) FROM c", timeout.Token)).ShouldHaveSingleItem()
            .ShouldBe(new object?[] { 1, 7300L, 900L });
        await session.ExecuteAsync("UPDATE c SET v = 'ok'", cancellationToken: timeout.Token);
        await session.ExecuteAsync("DELETE FROM p WHERE id = 1", cancellationToken: timeout.Token);
        await transaction.CommitAsync(timeout.Token);
        (await ReadAsync(session, "SELECT pid FROM c", timeout.Token)).ShouldHaveSingleItem()[0].ShouldBeNull();
    }

    private static Dictionary<string, object?> Parameters(string value) => new() { ["v"] = value };

    private static async Task<List<object?[]>> ReadAsync(SqlDatabaseSession session, string sql, CancellationToken cancellationToken)
    {
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(sql, cancellationToken: cancellationToken);
        return await SqlT1ScalarTests.ReadAsync(result);
    }
}
