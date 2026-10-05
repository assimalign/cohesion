using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
/// <para>
/// The guard compares the cost per child of cascading 16,000 children with that of
/// cascading 4,000 — a growth ratio, not an absolute time, so CPU speed and build
/// configuration cancel out. Linear time costs the same per child at both sizes; the
/// quadratic walk cost four times as much per child at four times the children. The
/// guard allows two. Every parent's children share one table and one index, so every
/// cascade runs against the same tree.
/// </para>
/// <para>
/// Each round times one block of four 4,000-child cascades, run back to back, against one
/// 16,000-child cascade. Both blocks delete 16,000 children, so under linear time they run
/// about as long, and a machine busy with other work stretches both alike. Timed one cascade
/// against the other, a 4,000-child cascade could finish inside a single time slice while the
/// 16,000-child one, four times as long, shared the CPU with whatever else ran: a three-core
/// runner in a parallel suite run measured 6.1 us per child for the small cascade and 12.7 for
/// the large one, 2.09 against the bound, with the engine unchanged. Pinned to three cores
/// beside three busy threads, the large cascade cost up to 2.2 times the fastest single small
/// cascade, while the two blocks stayed within 0.6 and 1.1 of each other. The quadratic walk
/// still costs four times as much in the large block, since each small cascade walks runs a
/// quarter as long.
/// </para>
/// <para>
/// A block is timed in the process's CPU time, not on the wall clock. The timing collection
/// runs alone in its process (<see cref="SqlTimingCollection"/>), so another process on the
/// machine stretches the wall clock but not the CPU this one spends deleting. On the wall
/// clock, with another engine's suite running on the same three cores, load swung each round's
/// cost two to four times: the engine unchanged reached 2.42 against the bound once in 40 runs,
/// and the duplicate-run walk #1194 removed passed it in 5 of 10. In CPU time the engine
/// unchanged measured 0.77 to 1.27, pinned to three cores or beside another suite, and the walk
/// 2.58 to 3.58, failing all 20 of its runs. Each block takes the fastest of three rounds, which
/// alternate which block runs first, after a warm-up round of both blocks that is not measured:
/// the warm-up's small block cost two to three times what later ones did, the cost of compiling
/// and tiering the paths it was first to run.
/// </para>
/// </remarks>
[Collection(SqlTimingCollection.Name)]
public sealed class SqlCascadeFanOutTests
{
    private const int smallFanOut = 4_000;
    private const int largeFanOut = 16_000;
    private const int smallPerBlock = largeFanOut / smallFanOut;
    private const int rounds = 3;
    private const double allowedGrowth = 2.0;

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Cascade: deleting a parent of 16,000 children costs per child what 4,000 children cost (#1194)")]
    public async Task Delete_WideFanOutCascade_ShouldTakeTimeLinearInChildren()
    {
        // Arrange: per round four small parents and one large parent, the first round a warm-up,
        // plus a bystander parent whose children must survive every cascade.
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "cascade-fan-out" });
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE p (id INT PRIMARY KEY)");
        await session.ExecuteAsync("CREATE TABLE c (id INT PRIMARY KEY, pid INT, CONSTRAINT fk_c FOREIGN KEY(pid) REFERENCES p(id) ON DELETE CASCADE)");
        await session.ExecuteAsync("CREATE INDEX c_pid ON c(pid)");

        var fanOuts = new List<int>();
        for (int round = 0; round <= rounds; round++)
        {
            fanOuts.AddRange(Enumerable.Repeat(smallFanOut, smallPerBlock));
            fanOuts.Add(largeFanOut);
        }

        fanOuts.Add(100);
        int bystander = fanOuts.Count;
        await session.ExecuteAsync($"INSERT INTO p VALUES {string.Join(", ", Enumerable.Range(1, fanOuts.Count).Select(id => $"({id})"))}");
        int nextChild = 1;
        for (int parent = 1; parent <= fanOuts.Count; parent++)
        {
            nextChild = await InsertChildrenAsync(session, parent, fanOuts[parent - 1], nextChild);
        }

        // Act: a warm-up round, then each measured round's two blocks, the round's order alternating.
        double small = double.MaxValue;
        double large = double.MaxValue;
        var measured = new List<string>();
        string warmUp = "";
        for (int round = 0; round <= rounds; round++)
        {
            int first = 1 + round * (smallPerBlock + 1);
            int[] smallParents = [.. Enumerable.Range(first, smallPerBlock)];
            int[] largeParent = [first + smallPerBlock];
            double roundSmall;
            double roundLarge;
            if (round % 2 == 0)
            {
                roundSmall = await CascadeAsync(session, smallParents, smallFanOut);
                roundLarge = await CascadeAsync(session, largeParent, largeFanOut);
            }
            else
            {
                roundLarge = await CascadeAsync(session, largeParent, largeFanOut);
                roundSmall = await CascadeAsync(session, smallParents, smallFanOut);
            }

            if (round == 0)
            {
                // The warm-up round pays for compiling and tiering the cascade's paths.
                warmUp = $"{roundSmall:F1}/{roundLarge:F1}";
                continue;
            }

            small = Math.Min(small, roundSmall);
            large = Math.Min(large, roundLarge);
            measured.Add($"{roundSmall:F1}/{roundLarge:F1}");
        }

        // Assert: linear in the child count, and the bystander's children are intact.
        (large / small).ShouldBeLessThan(allowedGrowth,
            $"{small:F1} us of CPU per child cascading {smallPerBlock} parents of {smallFanOut:N0} children, {large:F1} us per child cascading one of " +
            $"{largeFanOut:N0} (fastest of the rounds' small/large: {string.Join(", ", measured)}; warm-up round {warmUp})");
        (await ScalarAsync(session, "SELECT COUNT(*) FROM c")).ShouldBe(100L);
        (await ScalarAsync(session, $"SELECT COUNT(*) FROM c WHERE pid = {bystander}")).ShouldBe(100L);
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
    /// Deletes the parents one statement each, back to back, each cascading to its
    /// <paramref name="children"/> children, and returns the microseconds of the process's CPU
    /// time the statements took per child (the class remarks).
    /// </summary>
    private static async Task<double> CascadeAsync(IDatabaseSession session, int[] parents, int children)
    {
        var affected = new long[parents.Length];
        TimeSpan start = Environment.CpuUsage.TotalTime;
        for (int i = 0; i < parents.Length; i++)
        {
            affected[i] = (await session.ExecuteAsync($"DELETE FROM p WHERE id = {parents[i]}", cancellationToken: Timeout())).AffectedCount;
        }

        double elapsed = (Environment.CpuUsage.TotalTime - start).TotalMicroseconds;

        affected.ShouldAllBe(count => count == 1);
        foreach (int parent in parents)
        {
            (await ScalarAsync(session, $"SELECT COUNT(*) FROM c WHERE pid = {parent}")).ShouldBe(0L);
        }

        return elapsed / ((double)parents.Length * children);
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
