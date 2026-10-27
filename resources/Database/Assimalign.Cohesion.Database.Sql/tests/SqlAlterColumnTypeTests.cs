using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(SqlLockWaitCollection.Name)]
public sealed class SqlAlterColumnTypeTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - ALTER TYPE: old-row expressions, missing tails, dense layout and indexes publish together")]
    public async Task ExecuteAsync_UsingOldRow_ShouldRewriteAndRebuildIndexes()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("alter-type", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecAsync(session, "CREATE TABLE t(id INT PRIMARY KEY, gone INT, v TEXT NOT NULL)");
        await ExecAsync(session, "INSERT INTO t VALUES(1,99,'10'),(2,98,'20')");
        await ExecAsync(session, "ALTER TABLE t DROP COLUMN gone");
        await ExecAsync(session, "ALTER TABLE t ADD tail INT DEFAULT 7");
        await ExecAsync(session, "ALTER TABLE t ALTER tail SET DEFAULT 9");
        await ExecAsync(session, "CREATE UNIQUE INDEX ix ON t(v)");
        var previous = Table(database);
        ulong generation = database.Catalog.DdlGeneration;

        // Act
        await ExecAsync(session, "ALTER TABLE t ALTER COLUMN v SET DATA TYPE BIGINT USING CAST(t.v AS BIGINT)+id+tail");

        // Assert
        Table(database).ObjectId.ShouldNotBe(previous.ObjectId);
        Table(database).DroppedColumnOrdinals.ShouldBeEmpty();
        Table(database).Columns.ShouldAllBe(c => c.MissingValueLiteral == null);
        database.Catalog.DdlGeneration.ShouldBe(generation + 1);
        (await RowsAsync(session, "SELECT v,tail FROM t ORDER BY id")).ShouldBe(new[] { new object?[] { 18L, 7 }, new object?[] { 29L, 7 } });
        (await RowsAsync(session, "SELECT id FROM t WHERE v=18")).ShouldHaveSingleItem()[0].ShouldBe(1);
        session.LastStatementMetrics!.AccessPath.ShouldStartWith("seek:");
        await Should.ThrowAsync<DatabaseException>(() => ExecAsync(session, "INSERT INTO t VALUES(3,18,9)"));
        await ExecAsync(session, "INSERT INTO t(id,v) VALUES(3,30)");
        (await RowsAsync(session, "SELECT tail FROM t WHERE id=3")).ShouldHaveSingleItem()[0].ShouldBe(9);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - ALTER TYPE: implicit assignment casts and scale rewrites convert stored values")]
    [InlineData("INT", "7", "BIGINT", 7L)]
    [InlineData("NUMERIC(8,3)", "1.235", "NUMERIC(8,2)", "1.24")]
    [InlineData("CHAR(2)", "'x'", "CHAR(4)", "x   ")]
    [InlineData("INT", "7", "TEXT", "7")]
    public async Task ExecuteAsync_AssignmentCast_ShouldConvertValues(string source, string value, string target, object expected)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("alter-assignment", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecAsync(session, $"CREATE TABLE t(v {source})");
        await ExecAsync(session, $"INSERT INTO t VALUES({value})");

        // Act
        await ExecAsync(session, $"ALTER TABLE t ALTER v TYPE {target}");

        // Assert
        var actual = (await RowsAsync(session, "SELECT v FROM t")).ShouldHaveSingleItem()[0];
        if (target.StartsWith("NUMERIC", StringComparison.Ordinal)) { actual.ShouldBe(decimal.Parse((string)expected, System.Globalization.CultureInfo.InvariantCulture)); }
        else { actual.ShouldBe(expected); }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - ALTER TYPE: conversion and constraint failures leave the entire original owner current")]
    [InlineData("TEXT", "'x'", "INT USING CAST(v AS INT)")]
    [InlineData("INT NOT NULL", "1", "INT USING NULL")]
    [InlineData("INT CHECK(v>0)", "1", "INT USING -v")]
    [InlineData("TEXT", "'long'", "VARCHAR(2)")]
    [InlineData("INT UNIQUE", "1", "INT USING 0")]
    [InlineData("INT DEFAULT 999", "1", "TINYINT")]
    public async Task ExecuteAsync_InvalidConversion_ShouldPreserveOriginalAndAllowRetry(string source, string value, string target)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("alter-refusal", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecAsync(session, $"CREATE TABLE t(v {source})");
        await ExecAsync(session, $"INSERT INTO t VALUES({value})");
        if (source.Contains("UNIQUE", StringComparison.Ordinal)) { await ExecAsync(session, "INSERT INTO t VALUES(2)"); }
        var previous = Table(database);
        ulong generation = database.Catalog.DdlGeneration;

        // Act / Assert
        await Should.ThrowAsync<DatabaseException>(() => ExecAsync(session, $"ALTER TABLE t ALTER v TYPE {target}"));
        Table(database).ShouldBeSameAs(previous);
        database.Catalog.DdlGeneration.ShouldBe(generation);
        database.IndexManager.ExportRegistrations().ShouldAllBe(index => index.ObjectId == previous.ObjectId);
        await ExecAsync(session, "ALTER TABLE t ALTER v DROP DEFAULT");
        await ExecAsync(session, "ALTER TABLE t ALTER v TYPE " + SqlType.ForStorage(previous.Columns[0].Type).Name);
        (await RowsAsync(session, "SELECT v FROM t")).Count.ShouldBe(source.Contains("UNIQUE", StringComparison.Ordinal) ? 2 : 1);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - ALTER TYPE: no automatic text input cast, even on an empty table or with USING")]
    [InlineData("")]
    [InlineData(" USING v")]
    public async Task ExecuteAsync_ExplicitOnlyCast_ShouldRequireExplicitUsingCast(string clause)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("alter-no-cast", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecAsync(session, "CREATE TABLE t(v TEXT)");

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(() => ExecAsync(session, "ALTER TABLE t ALTER v TYPE INT" + clause));

        // Assert
        failure.Diagnostics.ShouldContain(d => d.Code == "CS02064");
        await ExecAsync(session, "ALTER TABLE t ALTER v TYPE INT USING CAST(v AS INT)");
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - ALTER TYPE: USING does not convert a dependent default")]
    public async Task ExecuteAsync_IncompatibleDefault_ShouldRefuseIndependentlyOfUsing()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("alter-default", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecAsync(session, "CREATE TABLE t(v TEXT DEFAULT '7')");

        // Act / Assert
        var failure = await Should.ThrowAsync<DatabaseException>(() => ExecAsync(session, "ALTER TABLE t ALTER v TYPE INT USING CAST(v AS INT)"));
        failure.Diagnostics.ShouldContain(d => d.Code == "CS02064");
        await ExecAsync(session, "ALTER TABLE t ALTER v DROP DEFAULT");
        await ExecAsync(session, "ALTER TABLE t ALTER v TYPE INT USING CAST(v AS INT)");
        await ExecAsync(session, "INSERT INTO t DEFAULT VALUES");
        (await RowsAsync(session, "SELECT v FROM t")).ShouldHaveSingleItem()[0].ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - ALTER TYPE: recast defaults remain executable after reopen")]
    [InlineData("7")]
    [InlineData("next_value()")]
    public async Task Reopen_RecastDefault_ShouldPreserveFutureAssignments(string expression)
    {
        // Arrange
        var strategy = new CrashCaptureSqlStorageStrategy();
        await using (var engine = SqlDatabaseEngine.Create("alter-reopen", new() { StorageStrategy = strategy }))
        {
            var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
            await using var session = await database.CreateSessionAsync(CancellationToken.None);
            await ExecAsync(session, "CREATE FUNCTION next_value() RETURNS INT VOLATILE RETURN 7");
            await ExecAsync(session, $"CREATE TABLE t(v INT DEFAULT {expression})");
            await ExecAsync(session, "INSERT INTO t DEFAULT VALUES");

            // Act
            await ExecAsync(session, "ALTER TABLE t ALTER v TYPE TEXT USING v+1");
            await ExecAsync(session, "INSERT INTO t DEFAULT VALUES");
        }
        await using var reopened = SqlDatabaseEngine.Create("alter-reopen", new() { StorageStrategy = strategy.CaptureDurableImages() });
        var opened = await reopened.OpenDatabaseAsync("db", CancellationToken.None);
        await using var reader = await opened.CreateSessionAsync(CancellationToken.None);
        await ExecAsync(reader, "INSERT INTO t DEFAULT VALUES");

        // Assert
        (await RowsAsync(reader, "SELECT v FROM t ORDER BY v")).Select(row => row[0]).ShouldBe(new object?[] { "7", "7", "8" });
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - ALTER TYPE: changed domains recheck converted values and CHECK bindings")]
    public async Task ExecuteAsync_DomainTarget_ShouldRevalidateRows()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("alter-domain", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecAsync(session, "CREATE DOMAIN positive AS INT CHECK(VALUE>0)");
        await ExecAsync(session, "CREATE TABLE t(v INT)");
        await ExecAsync(session, "INSERT INTO t VALUES(-1)");
        var previous = Table(database);

        // Act / Assert
        await Should.ThrowAsync<SqlConstraintViolationException>(() => ExecAsync(session, "ALTER TABLE t ALTER v TYPE positive"));
        Table(database).ShouldBeSameAs(previous);
        await ExecAsync(session, "ALTER TABLE t ALTER v TYPE positive USING -v");
        await Should.ThrowAsync<SqlConstraintViolationException>(() => ExecAsync(session, "INSERT INTO t VALUES(-1)"));
        (await RowsAsync(session, "SELECT v FROM t")).ShouldHaveSingleItem()[0].ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - ALTER TYPE: incoming, outgoing and self foreign keys validate the final replacement")]
    [InlineData("p", "id", "INT USING id+10")]
    [InlineData("c", "pid", "INT USING pid+10")]
    [InlineData("p", "id", "BIGINT")]
    [InlineData("c", "pid", "BIGINT")]
    public async Task ExecuteAsync_ForeignKeyChange_ShouldRefuseOrphanOrIncompatibleType(string table, string column, string target)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("alter-fk", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecAsync(session, "CREATE TABLE p(id INT PRIMARY KEY)");
        await ExecAsync(session, "CREATE TABLE c(pid INT REFERENCES p(id))");
        await ExecAsync(session, "INSERT INTO p VALUES(1)");
        await ExecAsync(session, "INSERT INTO c VALUES(1)");

        // Act / Assert
        await Should.ThrowAsync<DatabaseException>(() => ExecAsync(session, $"ALTER TABLE {table} ALTER {column} TYPE {target}"));
        (await RowsAsync(session, "SELECT id FROM p")).ShouldHaveSingleItem()[0].ShouldBe(1);
        await ExecAsync(session, $"ALTER TABLE {table} ALTER {column} TYPE INT USING {column}");
        await ExecAsync(session, "CREATE TABLE t(id INT PRIMARY KEY REFERENCES t(id))");
        await ExecAsync(session, "INSERT INTO t VALUES(1),(2)");
        await ExecAsync(session, "ALTER TABLE t ALTER id TYPE BIGINT USING id+10");
        (await RowsAsync(session, "SELECT id FROM t ORDER BY id")).Select(row => row[0]).ShouldBe(new object?[] { 11L, 12L });
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - ALTER TYPE: dependent views refuse all TYPE forms")]
    [InlineData("INT")]
    [InlineData("BIGINT")]
    [InlineData("INT USING v+1")]
    public async Task ExecuteAsync_ViewDependency_ShouldKeepOriginal(string target)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("alter-view", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecAsync(session, "CREATE TABLE t(v INT)");
        await ExecAsync(session, "CREATE VIEW view_t AS SELECT v FROM t");
        var previous = Table(database);

        // Act / Assert
        await Should.ThrowAsync<DatabaseException>(() => ExecAsync(session, "ALTER TABLE t ALTER v TYPE " + target));
        Table(database).ShouldBeSameAs(previous);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - ALTER TYPE: failures and cancellation at every preparation stage release locks and metadata")]
    [InlineData("Reserved", false)]
    [InlineData("Copied", false)]
    [InlineData("Prepared", false)]
    [InlineData("Reserved", true)]
    [InlineData("Copied", true)]
    [InlineData("Prepared", true)]
    public async Task ExecuteAsync_InterruptedRewrite_ShouldReleaseAllOwnership(string stage, bool cancel)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        await using var engine = SqlDatabaseEngine.Create("alter-interrupt", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await ExecAsync(session, "CREATE TABLE t(v INT PRIMARY KEY)");
        await ExecAsync(session, "INSERT INTO t VALUES(1),(2)");
        var previous = Table(database);
        int observed = 0;
        SqlPlanExecutor.RewriteStage = (point, _) =>
        {
            if (point != stage) { return; }
            observed++;
            if (cancel) { cancellation.Cancel(); }
            else { throw new InvalidOperationException("Injected ALTER TYPE failure"); }
        };
        try
        {
            // Act
            if (cancel) { await Should.ThrowAsync<OperationCanceledException>(async () => await session.ExecuteAsync("ALTER TABLE t ALTER v TYPE BIGINT", cancellationToken: cancellation.Token)); }
            else { await Should.ThrowAsync<InvalidOperationException>(async () => await session.ExecuteAsync("ALTER TABLE t ALTER v TYPE BIGINT", cancellationToken: cancellation.Token)); }
        }
        finally { SqlPlanExecutor.RewriteStage = null; }

        // Assert
        observed.ShouldBe(1);
        Table(database).ShouldBeSameAs(previous);
        database.IndexManager.ExportRegistrations().ShouldAllBe(index => index.ObjectId == previous.ObjectId);
        await ExecAsync(session, "CREATE FUNCTION after_failure() RETURNS INT IMMUTABLE RETURN 1");
        await ExecAsync(session, "ALTER TABLE t ALTER v TYPE BIGINT");
        (await RowsAsync(session, "SELECT v FROM t ORDER BY v")).Select(row => row[0]).ShouldBe(new object?[] { 1L, 2L });
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - ALTER TYPE: contending bound writers retry and preserve every committed update")]
    public async Task ExecuteAsync_ConcurrentBoundWriter_ShouldRetryWithoutLostWrites()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("alter-race", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await using var writer = await database.CreateSessionAsync(timeout.Token);
        await ExecAsync(session, "CREATE TABLE t(v INT NOT NULL)");
        await ExecAsync(session, "INSERT INTO t VALUES(0)");
        int waits = 0, retries = 0, commits = 0, rewrites = 0;
        Task? write = null;
        TaskCompletionSource? waited = null;
        using var listener = new LockWaitListener(database.Name);
        listener.Observed = value =>
        {
            if (value.EventName == "LockWaitStart") { Interlocked.Increment(ref waits); waited?.TrySetResult(); }
        };
        SqlPlanExecutor.RewriteStage = (point, _) =>
        {
            if (point != "Reserved") { return; }
            waited = new(TaskCreationOptions.RunContinuationsAsynchronously);
            write = Task.Run(async () =>
            {
                try { await writer.ExecuteAsync("UPDATE t SET v=v+1", cancellationToken: timeout.Token); }
                catch (DatabaseTransactionAbortedException failure)
                {
                    failure.Diagnostics.ShouldContain(d => d.Code == "CS04006");
                    Interlocked.Increment(ref retries);
                    await writer.ExecuteAsync("UPDATE t SET v=v+1", cancellationToken: timeout.Token);
                }
                Interlocked.Increment(ref commits);
            }, timeout.Token);
            waited.Task.WaitAsync(timeout.Token).GetAwaiter().GetResult();
        };
        var watch = Stopwatch.StartNew();
        try
        {
            // Act: time is a minimum/ceiling; observed waits, retries and commits decide completion.
            do
            {
                timeout.Token.ThrowIfCancellationRequested();
                await session.ExecuteAsync("ALTER TABLE t ALTER v TYPE " + (rewrites % 2 == 0 ? "BIGINT" : "INT"), cancellationToken: timeout.Token);
                rewrites++;
                await write!.WaitAsync(timeout.Token);
            } while (watch.Elapsed < TimeSpan.FromMilliseconds(250) || waits == 0 || retries == 0 || commits < 2);
        }
        finally
        {
            SqlPlanExecutor.RewriteStage = null;
            if (write is not null) { await write.WaitAsync(timeout.Token); }
        }

        // Assert
        waits.ShouldBeGreaterThan(0);
        retries.ShouldBe(rewrites);
        commits.ShouldBe(rewrites);
        Convert.ToInt64((await RowsAsync(session, "SELECT v FROM t")).ShouldHaveSingleItem()[0]).ShouldBe((long)commits);
        await ExecAsync(session, "CREATE FUNCTION after_race() RETURNS INT IMMUTABLE RETURN 1");
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - ALTER TYPE: registered assignment and implicit domain casts convert rows and defaults")]
    [InlineData(SqlCastContext.Assignment)]
    [InlineData(SqlCastContext.Implicit)]
    public async Task ExecuteAsync_RegisteredCast_ShouldConvertRowsAndDefaults(SqlCastContext context)
    {
        // Arrange
        var builder = SqlDatabaseEngine.CreateBuilder("alter-custom-cast");
        var domain = SqlType.CreateDomain("target_domain", SqlType.Integer);
        builder.Types.Add(domain).AddCast(SqlCast.Create<string, int>(SqlType.Text, domain,
            static value => int.Parse(value, System.Globalization.CultureInfo.InvariantCulture) + 1, context));
        await using var engine = builder.Build();
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecAsync(session, "CREATE TABLE t(v TEXT DEFAULT '7')");
        await ExecAsync(session, "INSERT INTO t DEFAULT VALUES");

        // Act
        await ExecAsync(session, "ALTER TABLE t ALTER v TYPE \"target_domain\"");
        await ExecAsync(session, "INSERT INTO t DEFAULT VALUES");

        // Assert
        (await RowsAsync(session, "SELECT v FROM t")).Select(row => row[0]).ShouldBe(new object?[] { 8, 8 });
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - ALTER TYPE: collation changes rebuild equality keys and refuse new duplicates")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_CollationChange_ShouldRebuildOrRefuseDuplicates(bool duplicate)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("alter-collation", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecAsync(session, "CREATE TABLE t(v TEXT COLLATE binary UNIQUE)");
        await ExecAsync(session, "INSERT INTO t VALUES('a')");
        if (duplicate) { await ExecAsync(session, "INSERT INTO t VALUES('A')"); }
        var previous = Table(database);

        // Act / Assert
        if (duplicate)
        {
            await Should.ThrowAsync<DatabaseException>(() => ExecAsync(session, "ALTER TABLE t ALTER v TYPE TEXT COLLATE case_insensitive"));
            Table(database).ShouldBeSameAs(previous);
        }
        else
        {
            await ExecAsync(session, "ALTER TABLE t ALTER v TYPE TEXT COLLATE case_insensitive");
            (await RowsAsync(session, "SELECT v FROM t WHERE v='A'")).ShouldHaveSingleItem()[0].ShouldBe("a");
            session.LastStatementMetrics!.AccessPath.ShouldStartWith("seek:");
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - ALTER TYPE: full-definition storage, scale and domain changes share the rewrite")]
    [InlineData("BIGINT", 7L)]
    [InlineData("NUMERIC(4,1)", "7.0")]
    public async Task ExecuteAsync_FullDefinition_ShouldUseSharedRewrite(string type, object expected)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("alter-full", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecAsync(session, "CREATE TABLE t(v INT)");
        await ExecAsync(session, "INSERT INTO t VALUES(7)");
        var previous = Table(database);

        // Act
        await ExecAsync(session, "ALTER TABLE t ALTER COLUMN v " + type);

        // Assert
        Table(database).ObjectId.ShouldNotBe(previous.ObjectId);
        var value = (await RowsAsync(session, "SELECT v FROM t")).ShouldHaveSingleItem()[0];
        value.ShouldBe(expected is string text ? decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture) : expected);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - ALTER TYPE: USING binds only the old table scalar scope even when empty")]
    [InlineData("SUM(v)")]
    [InlineData("(SELECT 1)")]
    [InlineData("other.v")]
    public async Task ExecuteAsync_InvalidUsingScope_ShouldRefuseBeforePublication(string expression)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("alter-scope", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecAsync(session, "CREATE TABLE t(v INT)");
        var previous = Table(database);

        // Act / Assert
        await Should.ThrowAsync<DatabaseException>(() => ExecAsync(session, "ALTER TABLE t ALTER v TYPE INT USING " + expression));
        Table(database).ShouldBeSameAs(previous);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - ALTER TYPE: incompatible CHECK operands refuse on empty tables")]
    public async Task ExecuteAsync_IncompatibleCheck_ShouldRefuseBeforeReadingRows()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("alter-check-type", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecAsync(session, "CREATE TABLE t(v INT CHECK(v>0))");
        var previous = Table(database);

        // Act / Assert
        var failure = await Should.ThrowAsync<DatabaseException>(() => ExecAsync(session, "ALTER TABLE t ALTER v TYPE TEXT"));
        failure.Diagnostics.ShouldContain(d => d.Code == "CS02055");
        Table(database).ShouldBeSameAs(previous);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - ALTER TYPE: character defaults keep every character and assignment overflow")]
    [InlineData("CHAR(4)", "'abcd'", "TEXT", "abcd")]
    [InlineData("TEXT", "'abcd'", "CHAR(5)", "abcd ")]
    [InlineData("INT", "1234", "CHAR(5)", "1234 ")]
    public async Task ExecuteAsync_CharacterDefault_ShouldPreserveWidth(string source, string value, string target, string expected)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("alter-char-default", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecAsync(session, $"CREATE TABLE t(v {source} DEFAULT {value})");

        // Act
        await ExecAsync(session, "ALTER TABLE t ALTER v TYPE " + target);
        await ExecAsync(session, "INSERT INTO t DEFAULT VALUES");

        // Assert
        (await RowsAsync(session, "SELECT v FROM t")).ShouldHaveSingleItem()[0].ShouldBe(expected);
        if (target.StartsWith("CHAR", StringComparison.Ordinal))
        {
            await Should.ThrowAsync<DatabaseException>(() => ExecAsync(session, "ALTER TABLE t ALTER v SET DEFAULT 'too long'"));
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - ALTER TYPE: volatile character defaults retain per-write assignment overflow")]
    public async Task ExecuteAsync_VolatileCharacterDefault_ShouldRefuseOverflowOnInsert()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("alter-volatile-char", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecAsync(session, "CREATE FUNCTION wide_default() RETURNS TEXT VOLATILE RETURN 'too long'");
        await ExecAsync(session, "CREATE TABLE t(v TEXT DEFAULT wide_default())");

        // Act
        await ExecAsync(session, "ALTER TABLE t ALTER v TYPE CHAR(2)");

        // Assert
        await Should.ThrowAsync<DatabaseException>(() => ExecAsync(session, "INSERT INTO t DEFAULT VALUES"));
        (await RowsAsync(session, "SELECT v FROM t")).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - ALTER TYPE: application CHAR defaults refuse before rewriting and can be dropped")]
    public async Task ExecuteAsync_ApplicationCharacterDefault_ShouldRequireSeparateDefaultDeclaration()
    {
        // Arrange
        var builder = SqlDatabaseEngine.CreateBuilder("alter-custom-char");
        var domain = SqlType.CreateDomain("target_char", SqlType.Char(4));
        builder.Types.Add(domain).AddCast(SqlCast.Create<string, string>(SqlType.Text, domain,
            static value => value.ToUpperInvariant(), SqlCastContext.Assignment));
        await using var engine = builder.Build();
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecAsync(session, "CREATE TABLE t(v TEXT DEFAULT 'abcd')");
        await ExecAsync(session, "INSERT INTO t DEFAULT VALUES");
        var previous = Table(database);

        // Act / Assert
        var failure = await Should.ThrowAsync<DatabaseException>(() => ExecAsync(session, "ALTER TABLE t ALTER v TYPE target_char"));
        failure.Diagnostics.ShouldContain(d => d.Code == "CK00001");
        Table(database).ShouldBeSameAs(previous);
        await ExecAsync(session, "ALTER TABLE t ALTER v DROP DEFAULT");
        await ExecAsync(session, "ALTER TABLE t ALTER v TYPE target_char");
        (await RowsAsync(session, "SELECT v FROM t")).ShouldHaveSingleItem()[0].ShouldBe("ABCD");
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - ALTER TYPE: lock cancellation and timeout release metadata and allow retry")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_LockWaitFailure_ShouldReleaseAdmission(bool cancel)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        await using var engine = SqlDatabaseEngine.Create("alter-wait", new() { LockTimeout = TimeSpan.FromMilliseconds(cancel ? 10000 : 50) });
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await using var holder = await database.CreateSessionAsync(timeout.Token);
        await ExecAsync(session, "CREATE TABLE t(v INT)");
        await using var transaction = await holder.BeginTransactionAsync(timeout.Token);
        await ExecAsync(holder, "INSERT INTO t VALUES(1)");
        var waited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = new LockWaitListener(database.Name);
        listener.Observed = value => { if (value.EventName == "LockWaitStart") { waited.TrySetResult(); } };
        var change = session.ExecuteAsync("ALTER TABLE t ALTER v TYPE BIGINT", cancellationToken: cancellation.Token).AsTask();

        // Act / Assert
        await waited.Task.WaitAsync(timeout.Token);
        if (cancel)
        {
            cancellation.Cancel();
            await Should.ThrowAsync<OperationCanceledException>(async () => await change);
        }
        else
        {
            var failure = await Should.ThrowAsync<SqlTimeoutException>(async () => await change);
            failure.Diagnostics.ShouldContain(d => d.Code == "CS04044");
        }
        await ExecAsync(session, "CREATE FUNCTION after_wait() RETURNS INT IMMUTABLE RETURN 1");
        await transaction.CommitAsync(timeout.Token);
        await ExecAsync(session, "ALTER TABLE t ALTER v TYPE BIGINT");
        (await RowsAsync(session, "SELECT v FROM t")).ShouldHaveSingleItem()[0].ShouldBe(1L);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - ALTER TYPE: final self-reference validation probes rebuilt unpublished indexes")]
    public async Task ExecuteAsync_SelfReferenceBackfill_ShouldUseRebuiltKeyLookups()
    {
        // Arrange
        const int count = 128;
        await using var engine = SqlDatabaseEngine.Create("alter-fk-lookups", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecAsync(session, "CREATE TABLE t(id INT PRIMARY KEY REFERENCES t(id))");
        await ExecAsync(session, "INSERT INTO t VALUES " + string.Join(',', Enumerable.Range(1, count).Select(value => $"({value})")));

        // Act
        await ExecAsync(session, "ALTER TABLE t ALTER id TYPE BIGINT USING id+1000");

        // Assert
        session.LastStatementMetrics!.ConstraintKeyComparisons.ShouldBeGreaterThanOrEqualTo(count);
        session.LastStatementMetrics.ConstraintKeyComparisons.ShouldBeLessThanOrEqualTo(count * 2);
        (await RowsAsync(session, "SELECT id FROM t WHERE id=1001")).ShouldHaveSingleItem()[0].ShouldBe(1001L);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - ALTER TYPE: integer identity widening preserves the allocation cursor")]
    public async Task ExecuteAsync_IdentityWidening_ShouldContinueAllocation()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("alter-identity", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecAsync(session, "CREATE TABLE t(id INT GENERATED ALWAYS AS IDENTITY PRIMARY KEY)");
        await ExecAsync(session, "INSERT INTO t DEFAULT VALUES");

        // Act
        await ExecAsync(session, "ALTER TABLE t ALTER id TYPE BIGINT");
        await ExecAsync(session, "INSERT INTO t DEFAULT VALUES");

        // Assert
        (await RowsAsync(session, "SELECT id FROM t ORDER BY id")).Select(row => row[0]).ShouldBe(new object?[] { 1L, 2L });
        await Should.ThrowAsync<DatabaseException>(() => ExecAsync(session, "ALTER TABLE t ALTER id TYPE TEXT"));
        await ExecAsync(session, "CREATE DOMAIN integer_domain AS BIGINT");
        await Should.ThrowAsync<DatabaseException>(() => ExecAsync(session, "ALTER TABLE t ALTER id TYPE integer_domain"));
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - ALTER TYPE: identity starts outside the new integer range refuse even when empty")]
    public async Task ExecuteAsync_IdentityStartOutsideTarget_ShouldRefuseBeforePublication()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("alter-identity-range", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecAsync(session, "CREATE TABLE t(id INT GENERATED ALWAYS AS IDENTITY (START WITH 1000))");
        var previous = Table(database);

        // Act / Assert
        await Should.ThrowAsync<DatabaseException>(() => ExecAsync(session, "ALTER TABLE t ALTER id TYPE TINYINT"));
        Table(database).ShouldBeSameAs(previous);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - ALTER TYPE: narrowed identity reservation endpoints reopen within the new type")]
    [InlineData(100, 1)]
    [InlineData(-100, -1)]
    public async Task Reopen_NarrowedIdentity_ShouldKeepValidCounterRecord(int start, int increment)
    {
        // Arrange
        var storage = new CrashCaptureSqlStorageStrategy();
        await using (var engine = SqlDatabaseEngine.Create("alter-identity-reopen", new() { StorageStrategy = storage }))
        {
            var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
            await using var session = await database.CreateSessionAsync(CancellationToken.None);
            await ExecAsync(session, $"CREATE TABLE t(id INT GENERATED ALWAYS AS IDENTITY (START WITH {start} INCREMENT BY {increment}))");
            await ExecAsync(session, "INSERT INTO t DEFAULT VALUES");

            // Act
            await ExecAsync(session, "ALTER TABLE t ALTER id TYPE TINYINT");
            await ExecAsync(session, "INSERT INTO t DEFAULT VALUES");
            (await RowsAsync(session, "SELECT id FROM t ORDER BY id")).Select(row => row[0]).ShouldBe(
                new object?[] { (sbyte)start, (sbyte)(start + increment) }.OrderBy(value => value));
        }
        await using var reopened = SqlDatabaseEngine.Create("alter-identity-reopen", new() { StorageStrategy = storage.CaptureDurableImages() });
        var restored = await reopened.OpenDatabaseAsync("db", CancellationToken.None);
        await using var reader = await restored.CreateSessionAsync(CancellationToken.None);

        // Assert: restart may skip the rest of the reserved block, but never rejects the catalog.
        (await RowsAsync(reader, "SELECT id FROM t ORDER BY id")).Select(row => row[0]).ShouldBe(
            new object?[] { (sbyte)start, (sbyte)(start + increment) }.OrderBy(value => value));
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - ALTER TYPE: an out-of-range identity cursor refuses after all its rows were deleted")]
    public async Task ExecuteAsync_IdentityCursorOutsideTarget_ShouldRetainTableAndAllocation()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("alter-identity-cursor", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecAsync(session, "CREATE TABLE t(id INT GENERATED ALWAYS AS IDENTITY (START WITH 120 INCREMENT BY 10))");
        await ExecAsync(session, "INSERT INTO t DEFAULT VALUES");
        await ExecAsync(session, "INSERT INTO t DEFAULT VALUES");
        await ExecAsync(session, "DELETE FROM t");
        var previous = Table(database);

        // Act / Assert
        var failure = await Should.ThrowAsync<DatabaseException>(() => ExecAsync(session, "ALTER TABLE t ALTER id TYPE TINYINT"));
        failure.Diagnostics.ShouldContain(d => d.Code == "CS02002");
        Table(database).ShouldBeSameAs(previous);
        await ExecAsync(session, "INSERT INTO t DEFAULT VALUES");
        (await RowsAsync(session, "SELECT id FROM t")).ShouldHaveSingleItem()[0].ShouldBe(140);
    }

    private static SqlCatalogTable Table(SqlDatabase database)
    {
        database.Catalog.TryGetTable("dbo", "t", out var table).ShouldBeTrue();
        return table;
    }

    private static async Task ExecAsync(SqlDatabaseSession session, string sql)
        => (await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None)).Status.ShouldBe(DatabaseResultStatus.Success);

    private static async Task<List<object?[]>> RowsAsync(SqlDatabaseSession session, string sql)
    {
        await using var result = (await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray()); }
        return rows;
    }
}
