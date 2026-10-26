using System.Collections.Generic;
using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Language.Tests;

public sealed class SqlDmlSourceParserTests
{
    [Theory(DisplayName = "Cohesion Test [Sql.Language] - DML sources: syntax and canonical rendering preserve complete trees (#1714)")]
    [InlineData("update dbo.t as x set v=s.v+1 from s, u where x.id=s.id returning x.id,s.v as old_value")]
    [InlineData("UPDATE t SET v = DEFAULT FROM s INNER JOIN u USING (id) LEFT JOIN w ON u.id = w.id CROSS JOIN z RETURNING *")]
    [InlineData("WITH q AS (SELECT id, v FROM s) UPDATE t SET v = q.v FROM q WHERE t.id = q.id RETURNING q.v")]
    [InlineData("UPDATE t SET v = q.v FROM (SELECT id, v FROM s UNION ALL SELECT id, v FROM u) AS q WHERE t.id = q.id")]
    [InlineData("delete from t as x using s, u where x.id=s.id returning x.id,s.v")]
    [InlineData("DELETE FROM t USING s JOIN u USING (id) LEFT JOIN w ON u.id = w.id RETURNING *")]
    [InlineData("WITH q AS (SELECT id FROM s) DELETE FROM t USING q WHERE t.id=q.id RETURNING q.id")]
    [InlineData("DELETE FROM t USING (SELECT id FROM s) q WHERE EXISTS (SELECT 1 WHERE q.id = t.id)")]
    [InlineData("UPDATE \"t\"\"x\" AS \"target\" SET \"v\"\"x\" = \"old\".\"v\"\"x\" FROM \"s\"\"x\" AS \"old\" RETURNING \"old\".\"v\"\"x\"")]
    [InlineData("UPDATE t SET v = old.v FROM s AS old RETURNING old.v")]
    [InlineData("DELETE FROM t USING s AS new RETURNING new.v")]
    public void Render_SourceCorpus_ShouldRoundTrip(string sql)
    {
        // Arrange
        var first = Parse(sql);

        // Act
        string rendered = Render(first);
        var second = Parse(rendered);

        // Assert
        Shape(second).ShouldBe(Shape(first));
        Render(second).ShouldBe(rendered);
        rendered.ShouldContain(first is SqlUpdateExpression ? " FROM " : " USING ", Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - DML sources: malformed lists fail syntax parsing (#1714)")]
    [InlineData("UPDATE t SET v = 1 FROM")]
    [InlineData("UPDATE t SET v = 1 FROM s,")]
    [InlineData("UPDATE t SET v = 1 FROM WHERE id = 1")]
    [InlineData("DELETE FROM t USING")]
    [InlineData("DELETE FROM t USING s, RETURNING id")]
    [InlineData("DELETE FROM t USING (SELECT id FROM s)")]
    public void Parse_MalformedSources_ShouldReportSyntax(string sql)
    {
        // Arrange / Act
        var statement = new SqlQueryParser().Parse(sql + ";");

        // Assert
        statement.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS00003");
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - DML sources: existing exclusions remain capability refusals (#1714)")]
    [InlineData("UPDATE t SET v = 1 FROM s RIGHT JOIN u ON s.id = u.id")]
    [InlineData("DELETE FROM t USING s FULL JOIN u ON s.id = u.id")]
    [InlineData("UPDATE t SET v = (SELECT v FROM s) FROM s")]
    [InlineData("UPDATE t SET v = s.v FROM s RETURNING (SELECT s.v)")]
    [InlineData("DELETE FROM t USING s RETURNING (SELECT s.id)")]
    public void Parse_ExcludedSources_ShouldReportUnsupported(string sql)
    {
        // Arrange / Act
        var statement = new SqlQueryParser().Parse(sql + ";");

        // Assert
        statement.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CK00001");
    }

    private static SqlQueryExpression Parse(string sql)
    {
        var statement = (SqlQueryStatement)new SqlQueryParser().Parse(sql + ";");
        statement.Diagnostics.ShouldNotContain(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        return statement.SqlExpression;
    }

    private static string Render(SqlQueryExpression query) => query is SqlUpdateExpression update
        ? SqlExpressionRenderer.Render(update) : SqlExpressionRenderer.Render((SqlDeleteExpression)query);

    private static string Shape(SqlQueryExpression query)
    {
        var update = query as SqlUpdateExpression;
        var delete = query as SqlDeleteExpression;
        var target = update?.Table ?? delete!.Table;
        var sources = update?.From ?? delete!.Using;
        var returning = update?.Returning ?? delete?.Returning;
        var definitions = update?.CommonTables ?? delete!.CommonTables;
        return Table(target) + ";" + string.Join(";", definitions.Select(definition => definition.Name + ":" +
            string.Join(",", definition.Columns) + ":" + SqlTreeDump.Dump(definition.Body))) + ";" +
            string.Join(";", (update?.Assignments ?? []).Select(assignment => assignment.ColumnName + ":" + SqlTreeDump.Dump(assignment.Value))) + ";" +
            string.Join(";", sources.Select(item => Table(item.Table) + ":" + string.Join(";", item.Joins.Select(join =>
                join.JoinType + ":" + Table(join.Table) + ":" + string.Join(",", join.UsingColumns) + ":" + SqlTreeDump.Dump(join.Condition))))) + ";" +
            SqlTreeDump.Dump(update?.Where ?? delete?.Where) + ";" +
            (returning is null ? "absent" : string.Join(";", returning.Select(column => column.Alias + ":" + SqlTreeDump.Dump(column.Expression))));
    }

    private static string Table(SqlRelationReference relation) => relation is SqlTableReference table
        ? $"{table.SchemaName}/{table.TableName}/{table.Alias}"
        : relation is SqlDerivedTableReference derived ? SqlTreeDump.Dump(derived.Query) + "/" + derived.Alias + "/" + string.Join(",", derived.Columns) : "?";
}
