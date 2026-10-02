using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>
/// Runs timing tests alone, so other suites do not compete for the CPU while they
/// measure.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SqlTimingCollection
{
    /// <summary>
    /// The collection name.
    /// </summary>
    public const string Name = "SQL timing";
}

/// <summary>
/// <c>ON DELETE CASCADE</c> over one parent's many children (#1194). Every child's row
/// version has an entry under the parent's key in the referencing column's index, and
/// the cascade tombstones each of them. Before #1194 a tombstone walked the key's run to
/// find its entry — past the entries the same cascade had already tombstoned — so a
/// cascade cost time quadratic in the parent's child count (4.5 s for 16,000 children
/// against 76 ms for 2,000). The index now descends to each entry, and the cascade is
/// linear in the child count.
/// </summary>
/// <remarks>
/// The guard compares the cost per child of cascading 16,000 children with that of
/// cascading 4,000 — a growth ratio, not an absolute time, so CPU speed, build
/// configuration and load cancel out. Linear time costs the same per child at both
/// sizes; the quadratic walk cost four times as much per child at four times the
/// children. The guard allows two. Both parents' children share one table and one
/// index, so both cascades run against the same tree, and each size takes the faster
/// of two rounds after a warm-up cascade.
/// </remarks>
[Collection(SqlTimingCollection.Name)]
public sealed class SqlCascadeFanOutTests
{
    private const int smallFanOut = 4_000;
    private const int largeFanOut = 16_000;
    private const double allowedGrowth = 2.0;

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Cascade: deleting a parent of 16,000 children costs per child what 4,000 children cost (#1194)")]
    public async Task Delete_WideFanOutCascade_ShouldTakeTimeLinearInChildren()
    {
        // Arrange: a warm-up parent, then two rounds of a small and a large parent, plus
        // a bystander parent whose children must survive every cascade.
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "cascade-fan-out" });
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE p (id INT PRIMARY KEY)");
        await session.ExecuteAsync("CREATE TABLE c (id INT PRIMARY KEY, pid INT, CONSTRAINT fk_c FOREIGN KEY(pid) REFERENCES p(id) ON DELETE CASCADE)");
        await session.ExecuteAsync("CREATE INDEX c_pid ON c(pid)");
        await session.ExecuteAsync("INSERT INTO p VALUES (1), (2), (3), (4), (5), (6)");

        int nextChild = 1;
        int[] fanOuts = [500, smallFanOut, largeFanOut, smallFanOut, largeFanOut, 100];
        for (int parent = 1; parent <= fanOuts.Length; parent++)
        {
            nextChild = await InsertChildrenAsync(session, parent, fanOuts[parent - 1], nextChild);
        }

        // Act
        await CascadeAsync(session, 1, fanOuts[0]);
        double small = Math.Min(await CascadeAsync(session, 2, smallFanOut), await CascadeAsync(session, 4, smallFanOut));
        double large = Math.Min(await CascadeAsync(session, 3, largeFanOut), await CascadeAsync(session, 5, largeFanOut));

        // Assert: linear in the child count, and the bystander's children are intact.
        (large / small).ShouldBeLessThan(allowedGrowth,
            $"{small:F1} us per child cascading {smallFanOut:N0} children, {large:F1} us per child cascading {largeFanOut:N0}");
        (await ScalarAsync(session, "SELECT COUNT(*) FROM c")).ShouldBe(100L);
        (await ScalarAsync(session, "SELECT COUNT(*) FROM c WHERE pid = 6")).ShouldBe(100L);
    }

    private static async Task<int> InsertChildrenAsync(IDatabaseSession session, int parent, int count, int firstChild)
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

    /// <summary>
    /// Deletes the parent, which cascades to its children, and returns the
    /// microseconds the statement took per child.
    /// </summary>
    private static async Task<double> CascadeAsync(IDatabaseSession session, int parent, int children)
    {
        long start = Stopwatch.GetTimestamp();
        var result = await session.ExecuteAsync($"DELETE FROM p WHERE id = {parent}", cancellationToken: Timeout());
        double elapsed = Stopwatch.GetElapsedTime(start).TotalMicroseconds;

        result.AffectedCount.ShouldBe(1);
        (await ScalarAsync(session, $"SELECT COUNT(*) FROM c WHERE pid = {parent}")).ShouldBe(0L);
        return elapsed / children;
    }

    private static async Task<object?> ScalarAsync(IDatabaseSession session, string sql)
    {
        await using var rows = (await session.ExecuteAsync(sql, cancellationToken: Timeout())).ShouldBeAssignableTo<QueryResultSet>();
        await foreach (var row in rows!.GetRowsAsync())
        {
            return row.GetValue(0);
        }

        return null;
    }

    private static CancellationToken Timeout() => TestTimeout.Token(300);
}
