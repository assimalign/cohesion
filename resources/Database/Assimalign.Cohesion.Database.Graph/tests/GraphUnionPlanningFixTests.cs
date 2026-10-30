using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Xunit;
using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed class GraphUnionPlanningFixTests
{
    [Fact(DisplayName = "Cohesion Test [Graph] - UNION: NEXT segments share the existing reduce planning cap")]
    public async Task ExecuteAsync_IndividuallyAdmittedSegments_ShouldSharePlanningCapAsync()
    {
        // Arrange: discover two independently admissible statements near the fixed cap.
        // Admission and diagnostics decide the boundary, independently of elapsed time.
        await using var engine = GraphDatabaseEngine.Create("union-next-plan-cap", new());
        var database = await engine.CreateDatabaseAsync("g", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        const string operand = "RETURN reduce(a=0,x IN [1,2] | a+x) AS value";
        string Linear(int count) => string.Join(" NEXT ", Enumerable.Repeat(operand, count));
        string Composite(int count) => string.Join(" UNION ALL ", Enumerable.Repeat(operand, count));
        int linearCount = await CapacityAsync(Linear);
        int compositeCount = await CapacityAsync(Composite);
        string combined = Linear(linearCount) + " NEXT (" + Composite(compositeCount) + ")";
        // Act / Assert: joining admitted segments must not reset the statement cap.
        var error = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync("EXPLAIN " + combined, cancellationToken: CancellationToken.None));
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG05026");
        session.LastStatementMetrics!.Physical.ShouldBe(0);
        session.LastStatementMetrics.Memory.RetainedBytes.ShouldBe(0);
        session.LastStatementMetrics.Memory.ActiveLeases.ShouldBe(0);

        async Task<int> CapacityAsync(Func<int, string> query)
        {
            int low = 1, high = 2;
            (await AdmitsAsync(query(low))).ShouldBeTrue();
            while (await AdmitsAsync(query(high)))
            {
                low = high;
                high *= 2;
                high.ShouldBeLessThanOrEqualTo(16384);
            }
            while (high - low > 1)
            {
                int middle = low + (high - low) / 2;
                if (await AdmitsAsync(query(middle))) { low = middle; }
                else { high = middle; }
            }
            return low;
        }

        async Task<bool> AdmitsAsync(string query)
        {
            try
            {
                await using var result = (DatabaseResultSet)await session.ExecuteAsync("EXPLAIN " + query, cancellationToken: CancellationToken.None);
                return true;
            }
            catch (DatabaseException error) when (error.Diagnostics.Count == 1 && error.Diagnostics[0].Code == "CG05026")
            { return false; }
        }
    }
}
