using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Indexing;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>
/// Guards #1194's direct descent to a duplicate-key entry using physical entry-directory
/// reads. Four 4,000-child cascades and one 16,000-child cascade delete equal totals in the
/// same tree. A duplicate-run walk performs quadratic work; direct descent stays bounded
/// per child. No duration or scheduling decision participates in the assertion (#1285).
/// </summary>
[Collection(SqlTimingCollection.Name)]
public sealed class SqlCascadeFanOutTests
{
    private readonly ITestOutputHelper _output;

    public SqlCascadeFanOutTests(ITestOutputHelper output) { _output = output; }

    private const int smallFanOut = 4_000;
    private const int largeFanOut = 16_000;
    private const int smallPerBlock = largeFanOut / smallFanOut;
    private const double allowedGrowth = 2.0;

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Cascade: deleting a parent of 16,000 children performs bounded physical index work per child compared with 4,000 children (#1194)")]
    public async Task Delete_WideFanOutCascade_ShouldBoundIndexWorkPerChild()
    {
        // Arrange: equal child totals at two fan-outs and a bystander that must survive.
        await using var engine = SqlDatabaseEngine.Create("cascade-fan-out", new SqlDatabaseEngineOptions
        {
            MaintenanceInterval = TimeSpan.FromHours(1),
            CheckpointInterval = TimeSpan.FromHours(1),
        });
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE p (id INT PRIMARY KEY)");
        await session.ExecuteAsync("CREATE TABLE c (id INT PRIMARY KEY, pid INT, CONSTRAINT fk_c FOREIGN KEY(pid) REFERENCES p(id) ON DELETE CASCADE)");
        await session.ExecuteAsync("CREATE INDEX c_pid ON c(pid)");

        var fanOuts = new List<int>(Enumerable.Repeat(smallFanOut, smallPerBlock)) { largeFanOut };
        fanOuts.Add(100);
        int bystander = fanOuts.Count;
        await session.ExecuteAsync($"INSERT INTO p VALUES {string.Join(", ", Enumerable.Range(1, fanOuts.Count).Select(id => $"({id})"))}");
        int nextChild = 1;
        for (int parent = 1; parent <= fanOuts.Count; parent++)
        {
            nextChild = await InsertChildrenAsync(session, parent, fanOuts[parent - 1], nextChild);
        }

        // Act: the counter covers every entry-directory read, including a sequential walk.
        double small = await CascadeAsync(session, [.. Enumerable.Range(1, smallPerBlock)], smallFanOut);
        double large = await CascadeAsync(session, [smallPerBlock + 1], largeFanOut);
        _output.WriteLine($"Directory reads/child: {small:F4} at {smallFanOut:N0}; {large:F4} at {largeFanOut:N0}; growth {large / small:F4}.");

        // Assert: physical work stays bounded per child as the duplicate run grows fourfold.
        small.ShouldBeGreaterThan(1);
        large.ShouldBeGreaterThan(1);
        (large / small).ShouldBeLessThan(allowedGrowth,
            $"{small:F2} directory reads/child for {smallFanOut:N0}; {large:F2} for {largeFanOut:N0}");
        (await ScalarAsync(session, "SELECT COUNT(*) FROM c")).ShouldBe(100L);
        (await ScalarAsync(session, $"SELECT COUNT(*) FROM c WHERE pid = {bystander}")).ShouldBe(100L);
    }

    private static async Task<int> InsertChildrenAsync(SqlDatabaseSession session, int parent, int count, int firstChild)
    {
        var sql = new StringBuilder();
        int end = firstChild + count;
        for (int first = firstChild; first < end; first += 250)
        {
            sql.Clear().Append("INSERT INTO c VALUES ");
            int last = Math.Min(end - 1, first + 249);
            for (int id = first; id <= last; id++)
            {
                sql.Append(id == first ? "" : ", ").Append(CultureInfo.InvariantCulture, $"({id}, {parent})");
            }

            await session.ExecuteAsync(sql.ToString(), cancellationToken: Timeout());
        }

        return end;
    }

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "set_EntryAccessed")]
    private static extern void SetEntryAccessed(BTreeIndex? declaringType, Action? observer);

    private static async Task<double> CascadeAsync(SqlDatabaseSession session, int[] parents, int children)
    {
        var affected = new long[parents.Length];
        long reads = 0;
        try
        {
            SetEntryAccessed(null, () => Interlocked.Increment(ref reads));
            for (int i = 0; i < parents.Length; i++)
            {
                affected[i] = (await session.ExecuteAsync($"DELETE FROM p WHERE id = {parents[i]}", cancellationToken: Timeout())).AffectedCount;
            }
        }
        finally
        {
            SetEntryAccessed(null, null);
        }

        affected.ShouldAllBe(count => count == 1);
        foreach (int parent in parents)
        {
            (await ScalarAsync(session, $"SELECT COUNT(*) FROM c WHERE pid = {parent}")).ShouldBe(0L);
        }

        return reads / ((double)parents.Length * children);
    }
    private static async Task<object?> ScalarAsync(SqlDatabaseSession session, string sql)
    {
        await using var rows = (await session.ExecuteAsync(sql, cancellationToken: Timeout())).ShouldBeAssignableTo<DatabaseResultSet>();
        await foreach (var row in rows!.GetRowsAsync())
        {
            return row.GetValue(0);
        }

        return null;
    }

    private static CancellationToken Timeout() => TestTimeout.Token(300);
}
