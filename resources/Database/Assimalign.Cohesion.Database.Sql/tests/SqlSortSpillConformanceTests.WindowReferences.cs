using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSortSpillConformanceTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - Reference-observing application callbacks keep the base window owner and fitting aliases (#1789)")]
    public async Task ExecuteAsync_WindowReferenceCallback_ShouldKeepBaseCapabilityAndIdentity()
    {
        foreach (long grant in new long[] { 262144, 524288, 1048576, 8388608 })
        {
            // Arrange
            var builder = SqlDatabaseEngine.CreateBuilder("window-reference-callback");
            builder.Options.TempDirectory = System.IO.Path.Combine(_root, "reference-callback-" + grant);
            builder.Options.StatementMemoryBudget = grant;
            builder.Functions.Add(SqlScalarFunction.Create<string,string,bool>("same_reference", ReferenceEquals));
            await using var engine = await builder.BuildAsync();
            var database = await engine.CreateDatabaseAsync("db");
            await using var session = await database.CreateSessionAsync(); await Seed(session);
            var outcomes = new List<(string?, long)>();
            foreach (bool forceBase in new[] { true, false })
            {
                database.Definitions.ForceSelectRetentionBasePath = forceBase;
                try
                {
                    // Act
                    await using var result = (DatabaseResultSet)await session.ExecuteAsync("SELECT same_reference(note,LAG(note,0) OVER(PARTITION BY id%100 ORDER BY id)) FROM t");
                    // Assert
                    var rows = await Rows(result); rows.Length.ShouldBe(4000); rows.ShouldAllBe(row => Equals(row.Cells[0],true));
                    outcomes.Add((null,session.LastStatementMetrics!.PeakHashMemoryBytes));
                }
                catch (DatabaseException failure) { outcomes.Add((failure.Diagnostics[0].Code,session.LastStatementMetrics!.PeakHashMemoryBytes)); }
                SqlSortSpillMetrics.For(session.LastStatementMetrics!).ShouldBeNull();
                session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0); TempBytes(engine.TempSpace!).ShouldBe(0);
            }
            outcomes[1].ShouldBe(outcomes[0]);
            if (grant == 8388608) { outcomes[1].Item1.ShouldBeNull(); }
        }
    }

}
