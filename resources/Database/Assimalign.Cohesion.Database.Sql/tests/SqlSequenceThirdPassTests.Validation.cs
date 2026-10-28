using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSequenceThirdPassTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Declared CHECK: sequence calls in subqueries retain the DDL refusal identity")]
    [InlineData("(SELECT nextval('s')) > 0")]
    [InlineData("EXISTS (SELECT 1 WHERE nextval('s') > 0)")]
    [InlineData("1 IN (SELECT nextval('s'))")]
    [InlineData("1 = ANY (SELECT nextval('s'))")]
    public void BindDeclaredChecks_SubquerySequenceCorpus_ShouldRefusePersistedDependency(string predicate)
    {
        // Arrange
        var schema = SqlSchema.Compile("app", builder => builder.Table<CheckRow>("t",
            table => { table.Column(row => row.K); table.Check("ck", predicate); }));
        int visits = 0;
        SqlSequenceExecution.PersistedNodeVisited = () => visits++;
        try
        {
            // Act
            var failure = Should.Throw<DatabaseException>(() => SqlSchemaProvisioner.BindDeclaredChecks(schema,
                Collation.Binary, SqlFunctionEnvironment.Standard, new SqlQueryParserOptions()));

            // Assert
            failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS03074");
            visits.ShouldBeGreaterThan(0);
            _output.WriteLine($"declared CHECK refusal=CS03074; visited nodes={visits}");
        }
        finally { SqlSequenceExecution.PersistedNodeVisited = null; }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Declared CHECK: refusal traversal enters subqueries once and stays linear")]
    [InlineData(64)][InlineData(256)]
    public void ValidateDeclaredCalls_SubqueryTree_ShouldVisitEachPersistedNodeOnce(int width)
    {
        // Arrange
        var statement = (SqlQueryStatement)new SqlQueryParser(new SqlQueryParserOptions { ExpressionNestingLimit = 1024 }).Parse(
            "SELECT (SELECT " + string.Join("+", Enumerable.Repeat("1", width)) + ") > 0");
        var predicate = ((SqlSelectExpression)statement.SqlExpression).Columns[0].Expression;
        int expected = SqlViewQuery.Nodes(predicate).Count();
        int visits = 0;
        SqlSequenceExecution.PersistedNodeVisited = () => visits++;
        try
        {
            // Act
            SqlPlanner.ValidateDeclaredCalls(predicate, SqlFunctionEnvironment.Standard.Catalog);

            // Assert
            visits.ShouldBe(expected);
            _output.WriteLine($"width={width}; persisted nodes={expected}; visits={visits}");
        }
        finally { SqlSequenceExecution.PersistedNodeVisited = null; }
    }

    private sealed class CheckRow { public int K { get; set; } }

}
