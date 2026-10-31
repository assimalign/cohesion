using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection("SQL bounded execution allocation")]
public sealed class SqlCopyToFixTests
{
    private readonly ITestOutputHelper _output;
    public SqlCopyToFixTests(ITestOutputHelper output) { _output = output; }

    [Fact(DisplayName = "Cohesion Test [Sql] - COPY: unrelated growth adds no visited pages or row allocations")]
    public async Task CopyToAsync_UnrelatedGrowth_ShouldVisitOnlyTargetPages()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        await using var engine = SqlDatabaseEngine.Create("copy-owner-scan", new());
        var database = await engine.CreateDatabaseAsync("app", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await session.ExecuteAsync("CREATE TABLE small(id INT)", cancellationToken: timeout.Token);
        await session.ExecuteAsync("INSERT INTO small VALUES " + string.Join(",", Enumerable.Range(0, 10).Select(i => "(" + i + ")")), cancellationToken: timeout.Token);
        database.Catalog.TryGetTable("dbo", "small", out var table).ShouldBeTrue();
        int targetPages = database.DataStorage.GetOwnerPages(table.ObjectId).Count;
        await session.ExecuteAsync("CREATE TABLE large(id INT)", cancellationToken: timeout.Token);
        StorageUnitIterator? scan = null;
        SqlPlanExecutor.CopyIteratorOpened = iterator => scan = iterator;
        var measurements = new System.Collections.Generic.List<(long Pages, long Bytes)>();
        try
        {
            foreach (int size in new[] { 0, 100000 })
            {
                if (size != 0)
                {
                    for (int start = 0; start < size; start += 1000)
                    {
                        await session.ExecuteAsync("INSERT INTO large VALUES " + string.Join(",", Enumerable.Range(start, 1000).Select(i => "(" + i + ")")), cancellationToken: timeout.Token);
                    }
                    await session.ExecuteAsync("CREATE INDEX ix_large ON large(id)", cancellationToken: timeout.Token);
                }
                await session.CopyToAsync("COPY small TO STDOUT", Stream.Null, timeout.Token);
                long minimum = long.MaxValue;
                // Act
                for (int round = 0; round < 3; round++)
                {
                    long before = GC.GetTotalAllocatedBytes(true);
                    (await session.CopyToAsync("COPY small TO STDOUT", Stream.Null, timeout.Token)).ShouldBe(10);
                    minimum = Math.Min(minimum, GC.GetTotalAllocatedBytes(true) - before);
                }
                long visited = PagesVisited(scan.ShouldNotBeNull());
                _output.WriteLine($"Unrelated rows {size}: COPY pages={visited}, target pages={targetPages}, allocation minimum={minimum} bytes.");
                measurements.Add((visited, minimum));
            }
            // Assert
            foreach (var measurement in measurements)
            {
                measurement.Pages.ShouldBe(targetPages);
                measurement.Bytes.ShouldBeLessThanOrEqualTo(100000);
            }
        }
        finally { SqlPlanExecutor.CopyIteratorOpened = null; }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - COPY: an already retired owner can reclaim during the export")]
    public async Task CopyToAsync_RetiredOwnerReclaimed_ShouldAvoidStalePageLookup()
    {
        // Arrange: discovery fences the retired owner before COPY enters its reader epoch.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var release = new ManualResetEventSlim();
        using var reclaimed = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var engine = SqlDatabaseEngine.Create("copy-reclamation", new());
        var database = await engine.CreateDatabaseAsync("app", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await session.ExecuteAsync("CREATE TABLE retired(id INT)", cancellationToken: timeout.Token);
        await session.ExecuteAsync("INSERT INTO retired VALUES(99)", cancellationToken: timeout.Token);
        database.Catalog.TryGetTable("dbo", "retired", out var retired).ShouldBeTrue();
        var pages = database.DataStorage.GetOwnerPages(retired.ObjectId).ToHashSet();
        await session.ExecuteAsync("CREATE TABLE live(id INT)", cancellationToken: timeout.Token);
        await session.ExecuteAsync("INSERT INTO live VALUES(1)", cancellationToken: timeout.Token);
        long before = database.TableReclamation.ReclaimedPages;
        database.TableReclamation.BeforeBatch = (owner, token) =>
        {
            if (owner != retired.ObjectId) { return; }
            entered.TrySetResult();
            release.Wait(token);
        };
        database.TableReclamation.AfterBatch = (owner, count) =>
        {
            if (owner == retired.ObjectId && count > 0) { reclaimed.Set(); }
        };
        int retiredReads = 0;
        try
        {
            await session.ExecuteAsync("DROP TABLE retired", cancellationToken: timeout.Token);
            await entered.Task.WaitAsync(timeout.Token);
            SqlPlanExecutor.CopyUnitRead = unit =>
            {
                // Reclaim during the export in both implementations: the old scan has
                // copied the retired row; the scoped scan has copied its live target.
                if (pages.Contains(unit.PageId)) { retiredReads++; }
                release.Set();
                reclaimed.Wait(timeout.Token);
            };
            using var output = new MemoryStream();
            // Act
            (await session.CopyToAsync("COPY live TO STDOUT", output, timeout.Token)).ShouldBe(1);
            // Assert
            Encoding.UTF8.GetString(output.ToArray()).ShouldBe("1\n");
            retiredReads.ShouldBe(0);
        }
        finally
        {
            release.Set();
            SqlPlanExecutor.CopyUnitRead = null;
            await Task.Run(() => reclaimed.Wait(timeout.Token), timeout.Token);
            database.TableReclamation.BeforeBatch = null;
            database.TableReclamation.AfterBatch = null;
        }
        (database.TableReclamation.ReclaimedPages - before).ShouldBe(pages.Count);
        database.Coordinator.GetOpenContexts().ShouldBeEmpty();
        _output.WriteLine($"Retired pages reclaimed={pages.Count}; retired rows read={retiredReads}.");
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - COPY: embedded execution shares export admission")]
    [InlineData(false, "UPDATE t SET id=id+10")]
    [InlineData(true, "UPDATE t SET id=id+10")]
    [InlineData(false, "BEGIN")]
    [InlineData(false, "SAVEPOINT s")]
    public async Task CopyToAsync_EmbeddedOverlap_ShouldRefuseExecution(bool typed, string sql)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("copy-overlap-fix", new());
        var database = await engine.CreateDatabaseAsync("app", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await session.ExecuteAsync("CREATE TABLE t(id INT)", cancellationToken: timeout.Token);
        await session.ExecuteAsync("INSERT INTO t VALUES(1),(2)", cancellationToken: timeout.Token);
        session.StatementTimeout = TimeSpan.FromHours(1);
        int timers = 0;
        session.StatementTimerArmed = _ => timers++;
        using var output = new SqlCopyGateStream();
        var copy = session.CopyToAsync("COPY t TO STDOUT", output, timeout.Token).AsTask();
        try
        {
            await output.Entered.Task.WaitAsync(timeout.Token);
            // Act / Assert
            await Should.ThrowAsync<InvalidOperationException>(async () =>
            {
                if (typed) { await session.ExecuteAsync(SqlQueryRequest.FromSql(sql), timeout.Token); }
                else { await session.ExecuteAsync(sql, cancellationToken: timeout.Token); }
            });
            timers.ShouldBe(1, "a refused overlap must not arm or replace the export's timer");
        }
        finally { output.Release.TrySetResult(); }
        (await copy.WaitAsync(timeout.Token)).ShouldBe(2);
        (await session.CopyToAsync("COPY t TO STDOUT", Stream.Null, timeout.Token)).ShouldBe(2);
        using var after = new MemoryStream();
        await session.CopyToAsync("COPY t TO STDOUT", after, timeout.Token);
        Encoding.UTF8.GetString(after.ToArray()).ShouldBe("1\n2\n");
        database.Coordinator.GetOpenContexts().ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - COPY: an admitted ordinary statement excludes an export")]
    public async Task CopyToAsync_StatementHeld_ShouldDescribeSessionHolder()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var engine = SqlDatabaseEngine.Create("copy-reverse-admission", new());
        var database = await engine.CreateDatabaseAsync("app", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await session.ExecuteAsync("CREATE TABLE t(id INT)", cancellationToken: timeout.Token);
        SqlQueryExecutor.BeforePlan = request =>
        {
            if (request.Statement.SqlExpression.Text != "INSERT INTO t VALUES(1)") { return; }
            entered.TrySetResult();
            release.Wait(timeout.Token);
        };
        var executing = Task.Run(async () => await session.ExecuteAsync("INSERT INTO t VALUES(1)", cancellationToken: timeout.Token), timeout.Token);
        try
        {
            await entered.Task.WaitAsync(timeout.Token);
            // Act
            var error = await Should.ThrowAsync<InvalidOperationException>(async () => await session.CopyToAsync("COPY t TO STDOUT", Stream.Null, timeout.Token));
            // Assert
            error.Message.ShouldContain("statement", Case.Sensitive);
        }
        finally { release.Set(); SqlQueryExecutor.BeforePlan = null; await executing.WaitAsync(timeout.Token); }
        (await session.CopyToAsync("COPY t TO STDOUT", Stream.Null, timeout.Token)).ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - COPY: deferred ADD waits before binding and snapshot capture")]
    public async Task CopyToAsync_DeferredColumn_ShouldExportPublishedLayout()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = SqlDatabaseEngine.CreateBuilder("copy-migration");
        builder.Options.StorageStrategy = new CrashCaptureSqlStorageStrategy();
        await using var engine = await builder.BuildAsync(timeout.Token);
        var database = await engine.CreateDatabaseAsync("app", timeout.Token);
        var items = new CompiledSchemaTable("items", "Item", [new("id", DatabaseType.Int32, false)], null, [], []);
        await database.ApplySchemaAsync(new(SqlCompiledSchema.CurrentFormat, "app", false, [], [items], []), timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await session.ExecuteAsync("INSERT INTO items VALUES(1)", cancellationToken: timeout.Token);
        var desired = new SqlCompiledSchema(SqlCompiledSchema.CurrentFormat, "app", true, [],
            [new("items", "Item", [.. items.Columns, new("fresh", DatabaseType.Int32, false, DefaultLiteral: "7")], null, [], [])], []);
        int steps = 0;
        database.SchemaProvisioner.BeforeDeferredStep = (_, token) =>
        {
            Interlocked.Increment(ref steps);
            entered.TrySetResult();
            release.Wait(token);
        };
        try
        {
            await database.SchemaProvisioner.ApplyAsync(desired, timeout.Token, deferDataScalingSteps: true);
            await entered.Task.WaitAsync(timeout.Token);
            using var output = new MemoryStream();
            // Act: admission reaches its real lock wait synchronously before yielding.
            var copy = session.CopyToAsync("COPY items TO STDOUT (FORMAT csv, HEADER)", output, timeout.Token).AsTask();
            try
            {
                steps.ShouldBe(1);
                copy.IsCompleted.ShouldBeFalse();
                output.Length.ShouldBe(0);
            }
            finally { release.Set(); }
            // Assert
            (await copy.WaitAsync(timeout.Token)).ShouldBe(1);
            Encoding.UTF8.GetString(output.ToArray()).ShouldBe("id,fresh\n1,7\n");
            database.Coordinator.GetOpenContexts().ShouldBeEmpty();
        }
        finally { release.Set(); }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - COPY: a held BEGIN is identified as transaction control")]
    public async Task CopyToAsync_BeginHeld_ShouldDescribeTransactionControl()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var engine = SqlDatabaseEngine.Create("copy-begin-holder", new());
        var database = await engine.CreateDatabaseAsync("app", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await session.ExecuteAsync("CREATE TABLE t(id INT)", cancellationToken: timeout.Token);
        SqlQueryExecutor.BeforeCatalogCapture = () => { entered.TrySetResult(); release.Wait(timeout.Token); };
        var beginning = Task.Run(async () => await session.BeginTransactionAsync(timeout.Token), timeout.Token);
        try
        {
            await entered.Task.WaitAsync(timeout.Token);
            // Act
            var error = await Should.ThrowAsync<InvalidOperationException>(async () => await session.CopyToAsync("COPY t TO STDOUT", Stream.Null, timeout.Token));
            // Assert
            error.Message.ShouldContain("transaction control", Case.Sensitive);
            error.Message.ShouldContain("BEGIN", Case.Sensitive);
        }
        finally
        {
            release.Set();
            SqlQueryExecutor.BeforeCatalogCapture = null;
            await using var transaction = await beginning.WaitAsync(timeout.Token);
            await transaction.RollbackAsync(timeout.Token);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - COPY: a large cast and cross-row Unicode remain byte-identical in bounded chunks")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CopyToAsync_LargeUnicodeCast_ShouldFillBoundedBuffers(bool csv)
    {
        // Arrange: split surrogate pairs and multibyte scalars at both character and byte boundaries.
        string rendered = new string('x', 16382) + "😀" + new string('中', 22000) + "é";
        var builder = SqlDatabaseEngine.CreateBuilder("copy-buffer-unicode");
        var type = SqlType.CreateDomain("export_value", SqlType.Integer);
        builder.Types.Add(type).AddCast(SqlCast.Create<int, string>(type, SqlType.Text, _ => rendered, SqlCastContext.Assignment));
        await using var engine = builder.Build();
        var database = await engine.CreateDatabaseAsync("app");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t(v export_value)");
        await session.ExecuteAsync("INSERT INTO t VALUES(1),(2),(3)");
        using var output = new SqlCopyGateStream();
        output.Release.TrySetResult();
        byte[] expected = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(rendered + "\n", 3)));
        // Act
        (await session.CopyToAsync("COPY t TO STDOUT (FORMAT " + (csv ? "csv" : "text") + ")", output)).ShouldBe(3);
        // Assert
        output.ToArray().ShouldBe(expected);
        output.MaximumWriteLength.ShouldBeLessThanOrEqualTo(16384);
        output.Writes.ShouldBe((expected.Length + 16380) / 16381);
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_PagesVisited")]
    private static extern long PagesVisited(StorageUnitIterator iterator);

    [Fact(DisplayName = "Cohesion Test [Sql] - COPY: XML contract describes destination cancellation and original failures")]
    public void CopyToAsync_XmlContract_ShouldDescribeDestinationRequirements()
    {
        // Arrange: review the actual public contract, including exception types.
        string path = SourceFile();
        var lines = File.ReadLines(path).SkipWhile(line => !line.Contains("/// <summary>Exports one table", StringComparison.Ordinal))
            .TakeWhile(line => !line.Contains("public async ValueTask", StringComparison.Ordinal))
            .Where(line => line.TrimStart().StartsWith("///", StringComparison.Ordinal)).Select(line => line[(line.IndexOf("///", StringComparison.Ordinal) + 3)..]);
        // Act
        var contract = System.Xml.Linq.XElement.Parse("<member>" + string.Join("\n", lines) + "</member>");
        // Assert
        contract.Elements("param").Single(element => (string?)element.Attribute("name") == "destination").Value.ShouldContain("honor cancellation", Case.Sensitive);
        contract.Element("remarks").ShouldNotBeNull().Value.ShouldContain("Session disposal cancels and waits", Case.Sensitive);
        contract.Elements("exception").Single(element => (string?)element.Attribute("cref") == "Exception").Value.ShouldContain("original exception", Case.Sensitive);
    }

    private static string SourceFile()
    {
        for (DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string path = Path.Combine(directory.FullName, "src", "SqlDatabaseSession.Copy.cs");
            if (File.Exists(path)) { return path; }
        }
        throw new InvalidOperationException("COPY public contract source was not found.");
    }
}
