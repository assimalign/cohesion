using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(SqlLockWaitCollection.Name)]
public sealed partial class SqlAlterColumnTypeReviewTests
{
    private readonly ITestOutputHelper _output;

    public SqlAlterColumnTypeReviewTests(ITestOutputHelper output) { _output = output; }

    [Theory(DisplayName = "Cohesion Test [Sql] - ALTER TYPE review: full definitions require explicit facet changes")]
    [InlineData("INT DEFAULT 0", "BIGINT")]
    [InlineData("INT DEFAULT 0", "BIGINT DEFAULT 1")]
    [InlineData("TEXT COLLATE case_insensitive UNIQUE", "VARCHAR(100)")]
    public async Task ExecuteAsync_FullDefinitionFacetChange_ShouldRefuse(string source, string target)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("alter-review-facets", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecAsync(session, $"CREATE TABLE t(v {source})");
        var previous = Table(database);

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(() => ExecAsync(session, "ALTER TABLE t ALTER COLUMN v " + target));

        // Assert
        failure.Diagnostics.ShouldContain(d => d.Code == "CS05013");
        Table(database).ShouldBeSameAs(previous);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - ALTER TYPE review: domain default assignment refuses overflow")]
    [InlineData("'abcdefg'")]
    [InlineData("next_text()")]
    public async Task ExecuteAsync_DomainDefaultOverflow_ShouldRetainOriginal(string expression)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("alter-review-overflow", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecAsync(session, "CREATE DOMAIN d5 AS VARCHAR(5)");
        await ExecAsync(session, "CREATE FUNCTION next_text() RETURNS TEXT IMMUTABLE RETURN 'abcdefg'");
        await ExecAsync(session, $"CREATE TABLE t(v TEXT DEFAULT {expression})");
        var previous = Table(database);

        // Act / Assert
        await Should.ThrowAsync<DatabaseException>(() => ExecAsync(session, "ALTER TABLE t ALTER v TYPE d5"));
        Table(database).ShouldBeSameAs(previous);
        await ExecAsync(session, "INSERT INTO t DEFAULT VALUES");
        (await RowsAsync(session, "SELECT v FROM t")).ShouldHaveSingleItem()[0].ShouldBe("abcdefg");
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - ALTER TYPE review: numeric and Boolean defaults keep expression identity")]
    [InlineData("7", "INT", 7)]
    [InlineData("TRUE", "BOOLEAN", true)]
    public async Task ExecuteAsync_TypedDefault_ShouldConvertOriginalExpression(string expression, string target, object expected)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("alter-review-literal", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecAsync(session, $"CREATE TABLE t(v TEXT DEFAULT {expression})");

        // Act
        await ExecAsync(session, $"ALTER TABLE t ALTER v TYPE {target} USING CAST(v AS {target})");
        await ExecAsync(session, "INSERT INTO t DEFAULT VALUES");

        // Assert
        (await RowsAsync(session, "SELECT v FROM t")).ShouldHaveSingleItem()[0].ShouldBe(expected);
        Table(database).Columns[0].DefaultLiteral.ShouldBe(expression);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - ALTER TYPE review: ten changes retain a canonical default and reopen")]
    public async Task Reopen_TenTypeChanges_ShouldKeepCanonicalDefault()
    {
        // Arrange
        var storage = new CrashCaptureSqlStorageStrategy();
        await using (var engine = SqlDatabaseEngine.Create("alter-review-canonical", new() { StorageStrategy = storage }))
        {
            var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
            await using var session = await database.CreateSessionAsync(CancellationToken.None);
            await ExecAsync(session, "CREATE TABLE t(v INT DEFAULT 7)");

            // Act
            for (int i = 0; i < 10; i++)
            { await ExecAsync(session, "ALTER TABLE t ALTER v TYPE " + (i % 2 == 0 ? "BIGINT" : "INT")); }

            // Assert
            Table(database).Columns[0].DefaultLiteral.ShouldBe("7");
        }
        await using var reopened = SqlDatabaseEngine.Create("alter-review-canonical", new() { StorageStrategy = storage.CaptureDurableImages() });
        var restored = await reopened.OpenDatabaseAsync("db", CancellationToken.None);
        await using var reader = await restored.CreateSessionAsync(CancellationToken.None);
        await ExecAsync(reader, "INSERT INTO t DEFAULT VALUES");
        (await RowsAsync(reader, "SELECT v FROM t")).ShouldHaveSingleItem()[0].ShouldBe(7);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - ALTER TYPE review: target domains supply their default")]
    public async Task ExecuteAsync_DomainWithoutDefault_ShouldInheritDefault()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("alter-review-inherit", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecAsync(session, "CREATE DOMAIN d AS INT DEFAULT 42 CHECK(VALUE>0)");
        await ExecAsync(session, "CREATE TABLE t(v INT)");

        // Act
        await ExecAsync(session, "ALTER TABLE t ALTER v TYPE d");
        await ExecAsync(session, "INSERT INTO t DEFAULT VALUES");

        // Assert
        Table(database).Columns[0].DefaultLiteral.ShouldBe("42");
        (await RowsAsync(session, "SELECT v FROM t")).ShouldHaveSingleItem()[0].ShouldBe(42);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - ALTER TYPE review: CHECK operators rebind on empty tables")]
    [InlineData("CASE v WHEN 'a' THEN TRUE ELSE FALSE END")]
    [InlineData("v LIKE 'a%'")]
    public async Task ExecuteAsync_IncompatibleCheck_ShouldRefuseBeforeReservation(string predicate)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("alter-review-check", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecAsync(session, $"CREATE TABLE t(v TEXT CHECK({predicate}))");
        var previous = Table(database);
        int reserved = 0;
        SqlPlanExecutor.RewriteStage = (stage, _) => { if (stage == "Reserved") { reserved++; } };
        try
        {
            // Act / Assert
            await Should.ThrowAsync<DatabaseException>(() => ExecAsync(session, "ALTER TABLE t ALTER v TYPE INT USING 1"));
            Table(database).ShouldBeSameAs(previous);
            reserved.ShouldBe(0);
        }
        finally { SqlPlanExecutor.RewriteStage = null; }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - ALTER TYPE review: compatible changes retain owner and indexes")]
    [InlineData("VARCHAR(20)", "VARCHAR(100)", "'abc'")]
    [InlineData("VARCHAR(20) COLLATE binary", "VARCHAR(100)", "'abc'")]
    [InlineData("NUMERIC(8,2)", "NUMERIC(10,2)", "1.25")]
    [InlineData("INT", "INT", "7")]
    public async Task ExecuteAsync_CompatibleType_ShouldKeepOwnerAndIndexes(string source, string target, string value)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("alter-review-compatible", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecAsync(session, $"CREATE TABLE t(v {source} UNIQUE)");
        await ExecAsync(session, $"INSERT INTO t VALUES({value})");
        var previous = Table(database);
        var indexes = database.IndexManager.ExportRegistrations().ToArray();
        var pages = database.DataStorage.GetOwnerPages(previous.ObjectId).ToArray();

        // Act
        await ExecAsync(session, "ALTER TABLE t ALTER v TYPE " + target);

        // Assert
        Table(database).ObjectId.ShouldBe(previous.ObjectId);
        database.IndexManager.ExportRegistrations().ShouldBe(indexes);
        database.DataStorage.GetOwnerPages(previous.ObjectId).ShouldBe(pages);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - ALTER TYPE review: widening ten thousand rows avoids copying")]
    public async Task ExecuteAsync_WideningTenThousandRows_ShouldAvoidCopy()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("alter-review-timing", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        foreach (string name in new[] { "t", "facet" })
        {
            await ExecAsync(session, $"CREATE TABLE {name}(id INT PRIMARY KEY, v VARCHAR(20))");
            await ExecAsync(session, $"CREATE INDEX ix_{name} ON {name}(v)");
            for (int start = 1; start <= 10000; start += 500)
            { await ExecAsync(session, $"INSERT INTO {name} VALUES " + string.Join(',', Enumerable.Range(start, 500).Select(n => $"({n},'row{n}')"))); }
        }
        long copied = 0;
        SqlPlanExecutor.RewriteStage = (stage, count) => { if (stage == "Copied") { copied += count; } };
        double type = double.MaxValue, facet = double.MaxValue;
        try
        {
            // Act: minima of five same-process rounds; durations are diagnostics only.
            for (int i = 0; i < 5; i++)
            {
                var watch = Stopwatch.StartNew();
                await ExecAsync(session, $"ALTER TABLE t ALTER v TYPE VARCHAR({100 + i})");
                type = Math.Min(type, watch.Elapsed.TotalMilliseconds);
                watch.Restart();
                await ExecAsync(session, $"ALTER TABLE facet ALTER COLUMN v VARCHAR({100 + i})");
                facet = Math.Min(facet, watch.Elapsed.TotalMilliseconds);
            }
        }
        finally { SqlPlanExecutor.RewriteStage = null; }

        // Assert
        _output.WriteLine($"10000 rows, two indexes, minima of five: TYPE={type:F4} ms; facet={facet:F4} ms; ratio={type / facet:F2}; copied progress sum={copied}");
        copied.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - ALTER TYPE review: unrelated FK neighbors stay writable without probes")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_UnrelatedForeignKeys_ShouldAvoidNeighborLocksAndScans(bool incoming)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("alter-review-fk-scope", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await using var writer = await database.CreateSessionAsync(timeout.Token);
        await ExecAsync(session, "CREATE TABLE p(id INT PRIMARY KEY)");
        await ExecAsync(session, "CREATE TABLE t(id INT PRIMARY KEY, pid INT REFERENCES p(id), notes INT)");
        await ExecAsync(session, "CREATE TABLE c(pid INT REFERENCES t(id))");
        await ExecAsync(session, "INSERT INTO p VALUES(1)");
        await ExecAsync(session, "INSERT INTO t VALUES(1,1,7)");
        await ExecAsync(session, "INSERT INTO c VALUES(1)");
        await using var transaction = await writer.BeginTransactionAsync(timeout.Token);
        int observed = 0;
        bool writable = false;
        var neighbor = database.Catalog.Tables.Single(t => t.Name == (incoming ? "c" : "p"));
        SqlPlanExecutor.RewriteStage = (stage, _) =>
        {
            if (stage != "Reserved") { return; }
            observed++;
            writable = database.Coordinator.LockManager.TryAcquire(transaction.Context.Sequence,
                LockResource.Object(neighbor.ObjectId), LockMode.IntentExclusive);
        };
        int decoded = 0;
        SqlPlanExecutor.RowDecoded = _ => decoded++;
        try
        {
            // Act
            await ExecAsync(session, "ALTER TABLE t ALTER notes TYPE BIGINT");

            // Assert
            observed.ShouldBe(1);
            writable.ShouldBeTrue();
            decoded.ShouldBe(1);
        }
        finally { SqlPlanExecutor.RewriteStage = null; SqlPlanExecutor.RowDecoded = null; }
        await transaction.RollbackAsync(timeout.Token);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - ALTER TYPE review: USING callbacks can write through another session")]
    public async Task ExecuteAsync_UsingCallbackWrite_ShouldEvaluateBeforeApplyGate()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        SqlDatabaseSession? writer = null;
        int calls = 0;
        var builder = SqlDatabaseEngine.CreateBuilder("alter-review-gate");
        builder.Functions.Add(SqlScalarFunction.Create("write_audit", (int value) =>
        {
            writer!.ExecuteAsync($"INSERT INTO audit VALUES({value})", cancellationToken: timeout.Token).AsTask().WaitAsync(timeout.Token).GetAwaiter().GetResult();
            calls++;
            return value;
        }));
        await using var engine = builder.Build();
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await using var other = await database.CreateSessionAsync(timeout.Token);
        writer = other;
        await ExecAsync(session, "CREATE TABLE audit(v INT)");
        await ExecAsync(session, "CREATE TABLE t(v INT)");
        await ExecAsync(session, "INSERT INTO t VALUES " + string.Join(',', Enumerable.Range(1, 65).Select(n => $"({n})")));

        // Act
        await session.ExecuteAsync("ALTER TABLE t ALTER v TYPE BIGINT USING write_audit(v)", cancellationToken: timeout.Token);

        // Assert
        calls.ShouldBe(65);
        (await RowsAsync(session, "SELECT v FROM audit")).Count.ShouldBe(65);
        (await RowsAsync(session, "SELECT v FROM t")).Count.ShouldBe(65);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - ALTER TYPE review: abandoned owners stay publicly pending until reclaimed")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RewriteTableAsync_AbandonedBuild_ShouldExposeAndReclaimOwner(bool typeChange)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("alter-review-abandon", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await ExecAsync(session, "CREATE TABLE t(v INT UNIQUE)");
        await ExecAsync(session, "INSERT INTO t VALUES(7)");
        ulong owner = 0;
        long epoch = database.TableReclamation.EnterStatement();
        SqlPlanExecutor.RewriteStage = (stage, id) =>
        {
            if (stage == "Reserved") { owner = checked((ulong)id); }
            if (stage == "Prepared") { throw new InvalidOperationException("Injected abandonment"); }
        };
        try
        {
            // Act
            await Should.ThrowAsync<InvalidOperationException>(async () =>
            {
                if (typeChange) { await ExecAsync(session, "ALTER TABLE t ALTER v TYPE BIGINT"); }
                else { await database.RewriteTableAsync("dbo", "t", timeout.Token); }
            });

            // Assert while reclamation is pinned.
            owner.ShouldNotBe(0UL);
            database.Catalog.GetPendingTableReclamations().ShouldContain(owner);
            database.DataStorage.HasOwnerPages(owner).ShouldBeTrue();
        }
        finally { SqlPlanExecutor.RewriteStage = null; database.TableReclamation.ExitStatement(epoch); }
        do
        {
            database.TableReclamation.RunBatch(timeout.Token);
            if (database.TableReclamation.HasWork) { await Task.Delay(1, timeout.Token); }
        } while (database.TableReclamation.HasWork);
        database.DataStorage.HasOwnerPages(owner).ShouldBeFalse();
        database.Catalog.GetPendingTableReclamations().ShouldNotContain(owner);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - ALTER TYPE review: missing domains retain readable expression defaults after reopen")]
    public async Task Reopen_UnregisteredDomainDefault_ShouldPermitReadsAndRefuseWrites()
    {
        // Arrange
        var storage = new CrashCaptureSqlStorageStrategy();
        var builder = SqlDatabaseEngine.CreateBuilder("alter-review-missing-domain");
        builder.Options.StorageStrategy = storage;
        builder.Types.Add(SqlType.CreateDomain("native_int", SqlType.Integer));
        await using (var engine = builder.Build())
        {
            var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
            await using var session = await database.CreateSessionAsync(CancellationToken.None);
            await ExecAsync(session, "CREATE TABLE t(v native_int DEFAULT (1+2))");
            await ExecAsync(session, "INSERT INTO t DEFAULT VALUES");
        }

        // Act
        await using var reopened = SqlDatabaseEngine.Create("alter-review-missing-domain", new() { StorageStrategy = storage.CaptureDurableImages() });
        var restored = await reopened.OpenDatabaseAsync("db", CancellationToken.None);
        await using var reader = await restored.CreateSessionAsync(CancellationToken.None);

        // Assert
        (await RowsAsync(reader, "SELECT v FROM t")).ShouldHaveSingleItem()[0].ShouldBe(3);
        var failure = await Should.ThrowAsync<DatabaseException>(() => ExecAsync(reader, "INSERT INTO t DEFAULT VALUES"));
        failure.Diagnostics.ShouldContain(d => d.Code == "CS02010");
    }

    private static SqlCatalogTable Table(SqlDatabase database) => database.Catalog.Tables.Single(t => t.Name == "t");

    private static async Task ExecAsync(SqlDatabaseSession session, string sql)
        => (await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None)).Status.ShouldBe(DatabaseResultStatus.Success);

    private static async Task<List<object?[]>> RowsAsync(SqlDatabaseSession session, string sql)
    {
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None);
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        { rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray()); }
        return rows;
    }
}
