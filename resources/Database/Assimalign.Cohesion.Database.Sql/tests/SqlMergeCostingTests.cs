using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(nameof(SqlDatabaseEventSourceCollection))]
public sealed class SqlMergeCostingTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - MERGE: wrapped statements refuse before costing reads source indexes (#1715)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_WrappedMerge_ShouldRefuseBeforeCostProbesAsync(bool correlated)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("merge-cost-refusal", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t(id INT,v INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE s(id INT,v INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE INDEX by_v ON s(v)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO s VALUES(1,1),(2,1),(3,2)", cancellationToken: CancellationToken.None);
        string sql = "MERGE INTO t USING s ON t.id=s.id AND EXISTS(SELECT id FROM s WHERE v=1" +
            (correlated ? " AND s.id=t.id" : "") + ") WHEN MATCHED THEN DELETE";
        int probes = 0, entries = 0;
        long matches = 0;
        SqlPlanExecutor.CostProbeCompleted = count => { probes++; entries += count.EntriesExamined; matches += count.Count; };
        try
        {
            // Prove that the fixture and observation detect a real cost probe.
            await using (var control = (await session.ExecuteAsync("SELECT id FROM s WHERE v=1", cancellationToken: CancellationToken.None))
                .ShouldBeAssignableTo<DatabaseResultSet>())
            {
                await foreach (var row in control.GetRowsAsync(CancellationToken.None)) { row.GetInt32(0).ShouldBeInRange(1, 2); }
            }
            probes.ShouldBe(1);
            matches.ShouldBe(2);
            entries.ShouldBeGreaterThanOrEqualTo(2);
            probes = entries = 0;
            matches = 0;

            // Act
            var failure = await Should.ThrowAsync<DatabaseException>(async () =>
                await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None));

            // Assert
            failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CK00001");
            probes.ShouldBe(0);
            entries.ShouldBe(0);
            matches.ShouldBe(0);
            session.LastStatementMetrics!.RecordsExamined.ShouldBe(0);
        }
        finally { SqlPlanExecutor.CostProbeCompleted = null; }
    }
}
