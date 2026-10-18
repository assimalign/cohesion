#if !DEBUG
using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(nameof(SqlDatabaseEventSourceCollection))]
public sealed class SqlViewWindowAllocationTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - View windows: cancellable INSERT seek and aggregate allocate zero extra startup bytes")]
    [InlineData("INSERT INTO sink SELECT id FROM items WHERE id = -1")]
    [InlineData("SELECT id FROM items WHERE id = 1")]
    [InlineData("SELECT COUNT(*) FROM items")]
    public async Task ExecuteAsync_NoWindowWithApplicationFunction_ShouldAddZeroBytes(string sql)
    {
        // Arrange: the empty INSERT SELECT exercises cancellable INSERT dispatch without page,
        // journal or ledger growth obscuring its startup allocations. The reference dispatcher
        // omits only window startup, retaining all current statement work.
        var builder = SqlDatabaseEngine.CreateBuilder("view-window-allocations");
        builder.Functions.Add(SqlScalarFunction.Create("application_value", static (int value) => value));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var actual = await engine.CreateDatabaseAsync("actual", CancellationToken.None);
        var reference = await engine.CreateDatabaseAsync("reference", CancellationToken.None);
        await SeedAsync(actual);
        await SeedAsync(reference);
        using var cancellation = new CancellationTokenSource();
        var actualTransaction = await actual.Coordinator.BeginAsync(IsolationLevel.Snapshot, CancellationToken.None);
        var referenceTransaction = await reference.Coordinator.BeginAsync(IsolationLevel.Snapshot, CancellationToken.None);
        try
        {
            var actualContext = new SqlStatementContext(actualTransaction, actual.Coordinator);
            var referenceContext = new SqlStatementContext(referenceTransaction, reference.Coordinator);
            var request = SqlQueryRequest.FromSql(sql);
            var actualPlan = new SqlPlanner(actual.Catalog, null, actual.Definitions.Functions).Plan(request.Statement.SqlExpression);
            var referencePlan = new SqlPlanner(reference.Catalog, null, reference.Definitions.Functions).Plan(request.Statement.SqlExpression);
            if (actualPlan is SqlSelectPlan select) { select.Access.ShouldBeOfType<SqlIndexSeekPath>(); }
            var executor = new SqlPlanExecutor(actual.DataStorage, actual.Catalog, actual.IndexManager, actual.Definitions);
            var baseline = new SqlPlanExecutor(reference.DataStorage, reference.Catalog, reference.IndexManager, reference.Definitions);
            Measure(100, false);
            Measure(100, true);

            // Act
            long actualBytes = long.MaxValue, referenceBytes = long.MaxValue;
            for (int round = 0; round < 3; round++)
            {
                referenceBytes = Math.Min(referenceBytes, Measure(1_000, true));
                actualBytes = Math.Min(actualBytes, Measure(1_000, false));
            }

            // Assert
            (actualBytes - referenceBytes).ShouldBe(0);

            long Measure(int count, bool useReference)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int index = 0; index < count; index++)
                {
                    var pending = useReference
                        ? SqlRowWindowAllocationBaseline.ExecuteAsync(baseline, referencePlan, referenceContext, cancellation.Token)
                        : executor.ExecuteAsync(actualPlan, actualContext, cancellation.Token);
                    if (!pending.IsCompletedSuccessfully) { throw new InvalidOperationException("The measured statement did not complete synchronously."); }
                    var result = pending.GetAwaiter().GetResult();
                    if (result.Status != DatabaseResultStatus.Success) { throw new InvalidOperationException("The measured statement failed."); }
                    if (result is DatabaseResultSet rows)
                    {
                        var dispose = rows.DisposeAsync();
                        if (!dispose.IsCompletedSuccessfully) { throw new InvalidOperationException("The measured result did not dispose synchronously."); }
                        dispose.GetAwaiter().GetResult();
                    }
                }
                return GC.GetAllocatedBytesForCurrentThread() - before;
            }
        }
        finally
        {
            await actual.Coordinator.RollbackAsync(actualTransaction, CancellationToken.None);
            await reference.Coordinator.RollbackAsync(referenceTransaction, CancellationToken.None);
        }

        static async Task SeedAsync(SqlDatabase database)
        {
            await using var session = await database.CreateSessionAsync(CancellationToken.None);
            await session.ExecuteAsync("CREATE TABLE items (id INT PRIMARY KEY)", cancellationToken: CancellationToken.None);
            await session.ExecuteAsync("INSERT INTO items VALUES (1)", cancellationToken: CancellationToken.None);
            await session.ExecuteAsync("CREATE TABLE sink (id INT)", cancellationToken: CancellationToken.None);
        }
    }
}
#endif
