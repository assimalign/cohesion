using System;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlTargetListParseLimitTests
{
    private readonly ITestOutputHelper _output;

    public SqlTargetListParseLimitTests(ITestOutputHelper output) { _output = output; }

    [Theory(DisplayName = "Cohesion Test [Sql] - Text to refusal: 16 MiB target lists stay bounded and leave the session usable (#1792)")]
    [InlineData("qualified")]
    [InlineData("explicit")]
    [InlineData("view")]
    [InlineData("returning")]
    public async Task Execute_LargeTargetText_ShouldRefuseBeforeRetainingTargets(string shape)
    {
        // Arrange: keep text construction outside the measurement, but include all parsing.
        await using var engine = SqlDatabaseEngine.Create("parse-target-bound", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t(a INT)", cancellationToken: CancellationToken.None);
        const int textBytes = 16 * 1024 * 1024;
        string prefix = shape == "view" ? "CREATE VIEW v AS SELECT " : shape == "returning" ? "DELETE FROM t RETURNING " : "SELECT ";
        string target = shape == "explicit" ? "a" : shape == "returning" ? "*" : "t.*";
        string suffix = shape == "returning" ? ";" : " FROM t;";
        int count = (textBytes - prefix.Length - suffix.Length + 1) / (target.Length + 1);
        var builder = new StringBuilder(textBytes).Append(prefix);
        for (int i = 0; i < count; i++) { if (i != 0) { builder.Append(','); } builder.Append(target); }
        builder.Append(suffix);
        builder.Append(' ', textBytes - builder.Length);
        string sql = builder.ToString();
        builder.Clear();

        // Act: parsing and its refusal complete synchronously on this thread, before any I/O.
        int thread = Environment.CurrentManagedThreadId;
        long before = GC.GetAllocatedBytesForCurrentThread();
        Exception? error = null;
        try
        {
            var unexpected = await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None);
            if (unexpected is DatabaseResultSet result) { await result.DisposeAsync(); }
        }
        catch (DatabaseException exception) { error = exception; }
        catch (OutOfMemoryException exception) { error = exception; }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        int completedThread = Environment.CurrentManagedThreadId;
        using var process = Process.GetCurrentProcess();
        _output.WriteLine($"{shape}: {sql.Length} ASCII bytes, {count} targets, {error?.GetType().Name}, allocated {allocated}, peak working set {process.PeakWorkingSet64}");
        await using var usable = (await session.ExecuteAsync("SELECT 1", cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        usable.Columns.Count.ShouldBe(1);
        int rows = 0;
        await foreach (var row in usable.GetRowsAsync(CancellationToken.None))
        { Convert.ToInt64(row.GetValue(0), System.Globalization.CultureInfo.InvariantCulture).ShouldBe(1L); rows++; }
        rows.ShouldBe(1);

        // Assert: 128 MiB leaves headroom for x64/Linux over the 32 MiB source-text copy.
        completedThread.ShouldBe(thread);
        var failure = error.ShouldBeAssignableTo<DatabaseException>().ShouldNotBeNull();
        failure.Diagnostics.Count.ShouldBe(1);
        var diagnostic = failure.Diagnostics.Single();
        diagnostic.Code.ShouldBe("CS05097");
        diagnostic.Start.ShouldBe(prefix.Length + 1664 * (target.Length + 1));
        diagnostic.End.ShouldBe(diagnostic.Start + (shape == "qualified" || shape == "view" ? 1 : target.Length));
        failure.Message.ShouldContain("1665");
        allocated.ShouldBeLessThan(128L * 1024 * 1024);
    }
}
