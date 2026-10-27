#if !DEBUG
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(nameof(SqlDatabaseEventSourceCollection))]
public sealed class SqlViewWindowAllocationTests
{
    private readonly ITestOutputHelper _output;

    public SqlViewWindowAllocationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - View windows: cancellable INSERT seek and aggregate allocate zero extra startup bytes")]
    [InlineData("INSERT INTO sink SELECT id FROM items WHERE id = -1")]
    [InlineData("SELECT id FROM items WHERE id = 1")]
    [InlineData("SELECT COUNT(*) FROM items")]
    public async Task ExecuteAsync_NoWindowWithApplicationFunction_ShouldAddZeroBytes(string sql)
    {
        // Arrange: the empty INSERT SELECT exercises cancellable INSERT dispatch without page
        // or ledger growth. Physical transaction brackets still append journal records; both
        // dispatchers retain that work, and the reference omits only window startup.
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
            var actualRounds = new (long Bytes, int Collections)[3];
            var referenceRounds = new (long Bytes, int Collections)[3];
            Measure(100, false);
            Measure(100, true);

            // Act
            long actualBytes = long.MaxValue, referenceBytes = long.MaxValue;
            for (int round = 0; round < actualRounds.Length; round++)
            {
                int collections = GC.CollectionCount(0);
                long measured = Measure(1_000, true);
                referenceRounds[round] = (measured, GC.CollectionCount(0) - collections);
                referenceBytes = Math.Min(referenceBytes, measured);
                collections = GC.CollectionCount(0);
                measured = Measure(1_000, false);
                actualRounds[round] = (measured, GC.CollectionCount(0) - collections);
                actualBytes = Math.Min(actualBytes, measured);
            }

            // Assert
            // Write only after all measurements, keeping output allocations out of every round.
            _output.WriteLine($"Architecture: {RuntimeInformation.ProcessArchitecture}; runtime: {RuntimeInformation.FrameworkDescription}.");
            for (int round = 0; round < actualRounds.Length; round++)
            {
                _output.WriteLine($"Round {round + 1}: actual {actualRounds[round].Bytes} bytes ({actualRounds[round].Collections} Gen0 collections); reference {referenceRounds[round].Bytes} bytes ({referenceRounds[round].Collections} Gen0 collections).");
            }
            _output.WriteLine($"Minima: actual {actualBytes} bytes; reference {referenceBytes} bytes; delta {actualBytes - referenceBytes} bytes.");
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
