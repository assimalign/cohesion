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
    [Theory(DisplayName = "Cohesion Test [Sql] - SQL functions and checked SQL domains conservatively keep the window owner (#1789)")]
    [InlineData("CREATE FUNCTION norm(s TEXT) RETURNS TEXT IMMUTABLE RETURN s")]
    [InlineData("CREATE DOMAIN positive AS INT CHECK(VALUE>0)")]
    public async Task ExecuteAsync_WindowSqlDefinitions_ShouldKeepBaseOwner(string definition)
    {
        // Arrange
        await using var engine = await CreateWindowAsync("window-sql-definitions", 262144);
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync(); await Seed(session);
        await session.ExecuteAsync(definition);
        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            "SELECT id,ROW_NUMBER() OVER(PARTITION BY id%100 ORDER BY id) FROM t"));
        // Assert
        database.Definitions.CanSpillWindowValues.ShouldBeFalse();
        failure.Diagnostics[0].Code.ShouldBe("CS05079");
        TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
    }

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
