using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlCommonTypeBatch05Tests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Grouped polymorphic alternatives pin the remaining concrete-metadata mismatch (#1788, #1789)")]
    [InlineData("COALESCE(unproved_result(id,CAST(NULL AS BIGINT)),0)")]
    [InlineData("CASE WHEN TRUE THEN unproved_result(id,CAST(NULL AS BIGINT)) ELSE 0 END")]
    public async Task ExecuteAsync_GroupedTypedNullPolymorphicResult_ShouldRetainExistingMetadataMismatch(string expression)
    {
        // Arrange: typed NULL resolves ANYELEMENT's metadata but its call can return text.
        var builder = SqlDatabaseEngine.CreateBuilder("grouped-polymorphic-followup");
        builder.Functions.Add(new HashUnprovedResultFunction(true, text: true));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        await using var session = await (await engine.CreateDatabaseAsync("db", CancellationToken.None)).CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t(id INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES(500)", cancellationToken: CancellationToken.None);

        // Act: KNOWN DEFECT witness in SqlPlanExecutor.Grouping.cs normalization:
        // grouped CASE/COALESCE passes text under Int64 metadata. Invert these
        // assertions when the normalization follow-up lands.
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(
            $"SELECT {expression},COUNT(*) FROM t GROUP BY id", cancellationToken: CancellationToken.None);

        // Assert: unresolved alternatives separately refuse CS02080.
        result.Columns[0].Type.ShouldBe(DatabaseType.Int64);
        int count = 0;
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        {
            row.GetValue(0).ShouldBe(new string('x', 100));
            row.GetValue(1).ShouldBe(1L);
            count++;
        }
        count.ShouldBe(1);

        // Act / Assert: the ungrouped counterpart enforces the resolved numeric family.
        var failure = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync($"SELECT {expression} FROM t", cancellationToken: CancellationToken.None));
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS02011");
    }
}
