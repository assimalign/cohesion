#if !DEBUG
using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Documents.Internal;

namespace Assimalign.Cohesion.Database.Documents.Tests;

[Collection(nameof(DocumentLikeCancellationCollection))]
public sealed class DocumentRejectedOrderingAllocationTests
{
    private readonly ITestOutputHelper _output;

    public DocumentRejectedOrderingAllocationTests(ITestOutputHelper output) { _output = output; }

    [Theory(DisplayName = "Cohesion Test [Documents] - Rejected ordered access retains the ordinary planning allocation budget (#1632)")]
    [InlineData("complex", "v INCLUDE MISSING", "", "v", 6616, 512)]
    [InlineData("mixed", "v INCLUDE MISSING, b", "WHERE v >= 0 AND b >= 0", "v, b DESC", 9872, 0)]
    [InlineData("missing", "v", "", "v", 6616, 0)]
    [InlineData("later", "v INCLUDE MISSING, b", "", "v, b", 7256, 0)]
    [InlineData("array", "DISTINCT ARRAY x FOR x IN arr END", "", "v", 6200, 0)]
    public async Task Plan_RejectedOrdering_ShouldKeepBaseAllocationBudgetAsync(string shape, string keys,
        string predicate, string order, int baseline, int probeAllowance)
    {
        // Arrange: the complex case needs a data probe; all other rejections are provable.
        await using var engine = DocumentDatabaseEngine.Create("rejected-order-allocations", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        for (int i = 0; i < 512; i++)
        {
            string v = shape == "complex" ? "[1,2]" : (i % 8).ToString(System.Globalization.CultureInfo.InvariantCulture);
            await collection.PutAsync(session, i.ToString("D3"), Encoding.UTF8.GetBytes(
                "{\"v\":" + v + ",\"b\":1,\"arr\":[1,2]}"), cancellationToken: CancellationToken.None);
        }
        await session.ExecuteAsync($"CREATE INDEX ix ON items ({keys})", cancellationToken: CancellationToken.None);
        var statement = DocumentQueryRequest.FromOql($"SELECT 1 FROM items {predicate} ORDER BY {order}").Statement.OqlExpression;

        // Act: parse/snapshot acquisition stay outside the synchronous measured loop.
        var measured = await database.RunAsync(session, operation =>
        {
            DocumentStatementPlan Plan() => new DocumentPlanner(database.Catalog, operation.Context.Snapshot, null).Plan(statement);
            for (int i = 0; i < 500; i++) { GC.KeepAlive(Plan()); }
            long bytes = long.MaxValue;
            double micros = double.MaxValue;
            for (int round = 0; round < 5; round++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                long started = Stopwatch.GetTimestamp();
                for (int i = 0; i < 500; i++) { GC.KeepAlive(Plan()); }
                long stopped = Stopwatch.GetTimestamp();
                bytes = Math.Min(bytes, GC.GetAllocatedBytesForCurrentThread() - before);
                micros = Math.Min(micros, (stopped - started) * 1_000_000d / Stopwatch.Frequency / 500);
            }
            ((DocumentPlan)Plan()).Access.ShouldNotBeOfType<DocumentOrderingPath>();
            return ValueTask.FromResult((Bytes: bytes / 500, Micros: micros));
        }, CancellationToken.None);

        // Assert: the allowance covers only the bounded complex-tag existence probe.
        _output.WriteLine(FormattableString.Invariant(
            $"Rejected {shape}: {measured.Bytes} B/plan; {measured.Micros:F2} us/plan; base={baseline}; allowance={probeAllowance}."));
        measured.Bytes.ShouldBeLessThanOrEqualTo((long)baseline + probeAllowance);
    }
}
#endif
