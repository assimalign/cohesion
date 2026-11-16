using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Testing;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlJointTypeMigrationTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - Copy pending pages remain charged until a checkpoint (#1778)")]
    public async Task ExecuteAsync_UncheckpointedCopyBrackets_ShouldRetainEveryAllocatedPage()
    {
        // Arrange: keep the whole rewrite below the forced checkpoint threshold.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var builder = Builder(new());
        builder.Options.CheckpointJournalSize = 0;
        builder.Options.CheckpointInterval = TimeSpan.FromDays(1);
        await using var engine = await builder.BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        await database.ApplySchemaAsync(WideCheckpointDeclaration(false), guard.Token);
        SeedRaw(database, [1, new string('x', 4000)], 1024);
        await WithPausedMaintenanceAsync(engine, guard.Token, async () =>
        {
            database.Coordinator.Checkpoint(guard.Token);
            var transaction = await database.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, guard.Token);
            var statement = new SqlStatementContext(transaction, database.Coordinator, provisioningSchema: "typed",
                provisioningProgress: (_, _) => { }, checkpointCopy: true);
            long start = database.TableReclamation.CaptureAllocationCount(), charged = 0;
            statement.ConfigureCopySpace((_, _, pending, _) => charged = pending, _ => { }, database.TableReclamation.CaptureAllocationCount);
            var executor = new SqlPlanExecutor(database.DataStorage, database.Catalog, database.IndexManager, database.Definitions);
            var samples = new List<(long Charged, long Allocated)>();
            using var events = new EventSourceRecorder(EventSource.GetSources().Single(value => value.Name == "Assimalign.Cohesion.Database.Storage"), EventLevel.Informational);
            SqlPlanExecutor.RewriteStage = (stage, _) =>
            {
                if (stage != "CopyCommitted") { return; }
                statement.CheckCopySpace(0);
                samples.Add((charged, database.TableReclamation.CaptureAllocationCount() - start));
            };
            try
            {
                // Act
                var plan = new SqlPlanner(database.Catalog, null).Plan(SqlQueryRequest.FromSql("ALTER TABLE items ALTER COLUMN Value TYPE BIGINT").Statement.SqlExpression);
                await executor.ExecuteAsync(plan, statement, guard.Token);

                // Assert: check every committed bracket, including the last one.
                samples.Count.ShouldBe(16);
                samples[^1].Allocated.ShouldBeGreaterThan(0);
                samples.Count(sample => sample.Charged != sample.Allocated).ShouldBe(0);
                events.Events.Count(entry => entry.EventId == 6 && Equals(entry.Payload![0], database.DataStorage.Name.ToString()) && Equals(entry.Payload[1], "Success")).ShouldBe(0);
                _output.WriteLine($"pending pages: brackets={samples.Count}; charged={samples[^1].Charged}; allocated={samples[^1].Allocated}; checkpoints=0");
            }
            finally
            {
                SqlPlanExecutor.RewriteStage = null;
                await database.Coordinator.RollbackAsync(transaction, CancellationToken.None);
            }
        });
    }
}
