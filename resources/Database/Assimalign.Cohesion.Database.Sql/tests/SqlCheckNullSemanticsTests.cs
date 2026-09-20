using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlCheckNullSemanticsTests
{
    [Fact]
    public async Task CheckAndWhere_ShouldApplyThreeValuedInPredicates()
    {
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "check-null" });
        var database = await engine.CreateDatabaseAsync("app");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t (id INT CHECK (id IN (1, NULL)))");
        // A true result and two unknown results satisfy CHECK.
        (await session.ExecuteAsync("INSERT INTO t VALUES (1), (2), (NULL)")).AffectedCount.ShouldBe(3);

        await using var included = (await session.ExecuteAsync("SELECT id FROM t WHERE id IN (1, NULL)"))
            .ShouldBeAssignableTo<QueryResultSet>();
        int matches = 0;
        await foreach (var row in included.GetRowsAsync())
        {
            row.GetValue(0).ShouldBe(1);
            matches++;
        }
        matches.ShouldBe(1);

        await using var excluded = (await session.ExecuteAsync("SELECT id FROM t WHERE id NOT IN (1, NULL)"))
            .ShouldBeAssignableTo<QueryResultSet>();
        int excludedMatches = 0;
        await foreach (var _ in excluded.GetRowsAsync())
        {
            excludedMatches++;
        }
        excludedMatches.ShouldBe(0);
    }
}
