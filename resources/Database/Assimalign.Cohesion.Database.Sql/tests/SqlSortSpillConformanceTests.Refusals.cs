using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSortSpillConformanceTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Spill unwind: reuse every-reservation fault and cancellation proof through the handoff (#1789)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_EveryReservation_ShouldUnwindSpill(bool deferred)
    {
        // Arrange / Act / Assert: the common helper tests every admission in both failure modes.
        await SqlSelectMemoryTests.VerifyEveryUnwind(deferred ? "SELECT record_value(k) FROM t ORDER BY k" : "SELECT k FROM t ORDER BY k", false, builder =>
        {
            builder.Options.StatementMemoryBudget = 196608;
            builder.Options.TempDirectory = Path.Combine(_root, ".unwind");
        }, async session =>
        {
            await session.ExecuteAsync("CREATE TABLE t(k INT)");
            await session.ExecuteAsync("INSERT INTO t VALUES" + string.Join(',', Enumerable.Repeat("(1)", deferred ? 1000 : 2000)));
        }, engine => { TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0); });
        if (Directory.Exists(Path.Combine(_root, ".unwind")))
        { Directory.GetFiles(Path.Combine(_root, ".unwind"), "*.ctmp").ShouldBeEmpty(); }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Spill collation: deferred ordering cannot select a snapshot before the final row (#1789)")]
    public async Task Add_DeferredCollation_ShouldKeepMemoryRefusal()
    {
        // Arrange
        await using var engine = await Create("deferred-collation", 262144);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        var frame = new SqlSubqueryValues();
        var term = new SqlCollationSubqueryTerm(0, Collation.CaseInsensitive, new SqlCollationCandidate(Collation.Binary, 1));
        var bound = SqlBoundCollation.Of(SqlCollationCandidate.Deferred(term), default, Collation.Binary);
        frame.Set(0, []);
        var metrics = new SqlStatementMetrics();
        using var collector = new SqlRowCollector([new(SqlBoundConstant.Null, false, true, bound)], frame, null, null, CancellationToken.None, metrics, 262144);

        // Act / Assert
        var failure = Should.Throw<DatabaseException>(() => { for (int i = 0; i < 4000; i++) { collector.Add([i], ["a"], session); } });
        failure.Diagnostics[0].Code.ShouldBe("CS05067"); collector.HasSpilled.ShouldBeFalse();
        collector.Dispose(); metrics.HashMemoryBytes.ShouldBe(0); TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Spill DISTINCT: distinct representatives themselves remain bounded (#1789)")]
    public async Task ExecuteAsync_DistinctSetExceedsGrant_ShouldRefuseAndRelease()
    {
        // Arrange
        await using var engine = await Create("distinct-refusal", 262144);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await Seed(session);

        // Act / Assert
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("SELECT DISTINCT id FROM t ORDER BY k"));
        failure.Diagnostics[0].Code.ShouldBe("CS05067"); SqlSortSpillMetrics.For(session.LastStatementMetrics!).ShouldNotBeNull();
        session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0); session.LastStatementMetrics.PlainSelectMemoryBytes.ShouldBe(0);
        TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
    }
}
