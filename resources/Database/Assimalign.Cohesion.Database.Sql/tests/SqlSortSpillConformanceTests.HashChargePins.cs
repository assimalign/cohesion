using System.IO;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSortSpillConformanceTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Fitting hash charge pins match c150bee5a with and without temp space (#1789)")]
    [InlineData(false, false, 744, 261888)]
    [InlineData(false, true, 744, 261888)]
    [InlineData(true, false, 1170, 262080)]
    [InlineData(true, true, 1170, 262080)]
    public async Task ExecuteAsync_HashFittingChargePins_ShouldKeepOriginalPeak(bool distinct, bool temp, int count, long peak)
    {
        // Arrange
        var builder = SqlDatabaseEngine.CreateBuilder("fitting-charge"); builder.Options.StatementMemoryBudget = 262144;
        if (temp) { builder.Options.TempDirectory = Path.Combine(_root, ".charge-pins"); }
        await using var engine = await builder.BuildAsync();
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync(); await Seed(session, count);

        // Act
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(distinct
            ? "SELECT DISTINCT id FROM t LIMIT 0" : "SELECT id,COUNT(*) FROM t GROUP BY id LIMIT 0");

        // Assert
        (await Rows(result)).ShouldBeEmpty(); session.LastStatementMetrics!.PeakHashMemoryBytes.ShouldBe(peak);
        session.LastStatementMetrics.HashMemoryBytes.ShouldBe(0); session.LastStatementMetrics.PlainSelectMemoryBytes.ShouldBe(0);
        _output.WriteLine($"CHARGE_PIN|distinct={distinct}|temp={temp}|rows={count}|peak={peak}");
    }
}
