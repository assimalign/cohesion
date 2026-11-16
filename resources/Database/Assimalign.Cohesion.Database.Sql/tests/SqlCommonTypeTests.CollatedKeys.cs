using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlCommonTypeTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - CASE/COALESCE: collated key slots retain source overload types in SELECT, HAVING and ORDER BY (#1788)")]
    public async Task Execute_CollatedKeySlots_ShouldUseResolvedSourceType()
    {
        // Arrange: the source CASE selects the numeric overload, which returns TEXT.
        // Rebinding the key's inner call as grouped output would select INTEGER.
        var builder = SqlDatabaseEngine.CreateBuilder("collated-alternative-key");
        builder.Functions.Add(SqlScalarFunction.Create("mixed_key", static (int value) => value, SqlFunctionVolatility.Immutable));
        builder.Functions.Add(SqlScalarFunction.Create("mixed_key", static (decimal value) => "d", SqlFunctionVolatility.Immutable));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await Execute(session, "CREATE TABLE t(c BOOLEAN)");
        await Execute(session, "INSERT INTO t VALUES(TRUE),(FALSE)");
        const string key = "mixed_key(CASE WHEN c THEN 1 ELSE 2.5 END) COLLATE binary";
        foreach (string grouping in new[] { key, $"ROLLUP({key})", $"CUBE({key})", $"GROUPING SETS(({key}),())" })
        {
            // Act: PostgreSQL 17 returns (D,2), and also (X,2) for the grand total.
            var actual = await Pass2Observe(session, null,
                $"SELECT UPPER(COALESCE({key},'x')),COUNT(*) FROM t GROUP BY {grouping} HAVING COALESCE({key},'x') IN ('d','x') ORDER BY COALESCE({key},'x')");

            // Assert
            actual.Types.ShouldBe(new[] { DatabaseType.String, DatabaseType.Int64 });
            actual.Rows.ShouldBe(grouping == key ? new object?[][] { ["D", 2L] } : [["D", 2L], ["X", 2L]]);
        }
    }
}
