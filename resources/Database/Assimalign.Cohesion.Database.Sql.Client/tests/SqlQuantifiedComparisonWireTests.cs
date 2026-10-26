using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

public sealed class SqlQuantifiedComparisonWireTests
{
    [Theory(DisplayName = "Cohesion Test [Sql.Client] - Quantified comparisons: complete truth tables survive InMemory and TCP")]
    [InlineData(false, "ANY")]
    [InlineData(false, "SOME")]
    [InlineData(false, "ALL")]
    [InlineData(true, "ANY")]
    [InlineData(true, "SOME")]
    [InlineData(true, "ALL")]
    public async Task QueryAsync_QuantifiedTruthTable_ShouldPreserveNullAndEmptyResults(bool tcp, string quantifier)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync(tcp: tcp);
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout(30));
        foreach (var (op, yes, no) in new[] { ("=", 2, 1), ("<>", 1, 2), ("!=", 1, 2), ("<", 3, 1), ("<=", 2, 1), (">", 1, 3), (">=", 2, 3) })
        {
            // Independent truth tables: empty, true, false, unknown, then deciding/unknown mixtures.
            bool?[][] sets = [[], [true], [false], [null], [true, null], [false, null], [null, true, false], [false, true, null]];
            bool?[] any = [false, true, false, null, true, null, true, true];
            bool?[] all = [true, true, false, null, null, false, false, false];
            for (int index = 0; index < sets.Length; index++)
            {
                string source = sets[index].Length == 0 ? "SELECT CAST(NULL AS INT) WHERE FALSE" :
                    string.Join(" UNION ALL ", sets[index].Select(value => "SELECT " +
                        (value is null ? "CAST(NULL AS INT)" : value.Value ? yes.ToString(CultureInfo.InvariantCulture) : no.ToString(CultureInfo.InvariantCulture))));
                // Act
                var rows = await QueryAsync(connection, $"SELECT 2 {op} {quantifier} ({source}), CAST(NULL AS INT) {op} {quantifier} ({source});");

                // Assert
                rows.Columns.Select(column => column.Type).ShouldAllBe(type => type == DatabaseType.Boolean);
                rows.Select(row => Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray()).ShouldHaveSingleItem().ShouldBe(new object?[]
                    { quantifier == "ALL" ? all[index] : any[index], index == 0 ? quantifier == "ALL" : null });
            }
        }

    }

    [Theory(DisplayName = "Cohesion Test [Sql.Client] - Quantified comparisons: correlated children, coded errors and connection reuse survive both transports")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_QuantifiedCorrelationAndErrors_ShouldKeepConnectionUsable(bool tcp)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync(tcp: tcp);
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout(30));

        // Act / Assert
        var rows = await QueryAsync(connection, "SELECT u.id, u.id >= ALL (SELECT v.id FROM users v WHERE v.id <= u.id), u.id = SOME (SELECT v.id FROM users v WHERE v.id > u.id) FROM users u ORDER BY u.id;");
        rows.Select(row => row[1]).ShouldBe(new object?[] { true, true });
        rows.Select(row => row[2]).ShouldBe(new object?[] { false, false });
        foreach (var (query, code) in new[] { ("SELECT 1 = ANY (SELECT id, name FROM users WHERE FALSE)", "CS05013"), ("SELECT 1 = ALL (SELECT missing FROM users)", "CS01015"), ("SELECT 1 = SOME (SELECT name FROM users)", "CS02055") })
        {
            var error = await Should.ThrowAsync<SqlClientException>(() => QueryAsync(connection, query));
            error.Diagnostics.Select(d => d.Code).ShouldContain(code, error.Message);
            error.ConnectionUsable.ShouldBeTrue();
        }
        (await QueryAsync(connection, "SELECT id FROM users ORDER BY id;")).Select(row => row[0]).ShouldBe(new object?[] { 1, 2 });
    }

    private static Task<SqlResultSet> QueryAsync(SqlConnection connection, string sql)
        => connection.QueryAsync(sql, cancellationToken: SqlClientTestHarness.Timeout(30)).AsTask();
}
