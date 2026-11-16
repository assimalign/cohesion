using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlCommonTypeTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - CASE/COALESCE: grouped native arguments retain base value families (#1788)")]
    [InlineData("CASE WHEN TRUE THEN 1 ELSE 2.5 END", "Int32")]
    [InlineData("CASE WHEN FALSE THEN 1 ELSE 2.5 END", "Decimal")]
    [InlineData("CASE WHEN TRUE THEN -1 ELSE 2.5 END", "Int64")]
    [InlineData("COALESCE(1,2.5)", "Int32")]
    [InlineData("COALESCE(-1,2.5)", "Int64")]
    [InlineData("COALESCE(1::SMALLINT,2)", "Int16")]
    [InlineData("COALESCE(NULL::SMALLINT,2)", "Int32")]
    [InlineData("COALESCE(MAX(NULL),2)", "Int32")]
    [InlineData("CASE WHEN TRUE THEN NULL ELSE NULL END", "Null")]
    [InlineData("COALESCE(CASE WHEN TRUE THEN NULL ELSE NULL END,2)", "Int32")]
    [InlineData("COALESCE(COALESCE(NULL,NULL),2)", "Int32")]
    [InlineData("CASE WHEN TRUE THEN 2 ELSE COALESCE(NULL,NULL) END", "Int32")]
    [InlineData("CASE WHEN TRUE THEN DATE '2001-01-02' ELSE TIMESTAMP '2001-01-02 00:00:00' END", "DateTime")]
    [InlineData("COALESCE(DATE '2001-01-02',NULL::TIMESTAMP)", "DateTime")]
    [InlineData("CASE WHEN TRUE THEN TIMESTAMP '2001-01-02 00:00:00' ELSE TIMESTAMPTZ '2001-01-02 00:00:00+00:00' END", "DateTimeOffset")]
    [InlineData("COALESCE(TIMESTAMP '2001-01-02 00:00:00',NULL::TIMESTAMPTZ)", "DateTimeOffset")]
    public async Task Execute_GroupedNativeArguments_ShouldRetainBaseValueFamily(string expression, string expected)
    {
        // Arrange: native AnyElement observes the value before projected-row normalization.
        var builder = SqlDatabaseEngine.CreateBuilder("grouped-alternative-abi");
        builder.Functions.Add(new AlternativeValueTypeFunction());
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await Execute(session, "CREATE TABLE t(id INT)");
        await Execute(session, "INSERT INTO t VALUES(1)");

        foreach (string grouping in new[] { "", " GROUP BY id", " GROUP BY ROLLUP(id)", " GROUP BY CUBE(id)", " GROUP BY GROUPING SETS((id),())" })
        {
            // Act
            var actual = await Pass2Observe(session, null, $"SELECT alternative_value_type({expression}),COUNT(*) FROM t{grouping}");

            // Assert
            actual.Types.ShouldBe(new[] { DatabaseType.String, DatabaseType.Int64 });
            actual.Rows.Length.ShouldBe(grouping.Contains('(') ? 2 : 1);
            foreach (var row in actual.Rows) { row.ShouldBe(new object?[] { expected, 1L }); }
        }
    }
}
