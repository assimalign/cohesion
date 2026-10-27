using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Language.Tests;

public sealed class SqlMergeParserTests
{
    [Fact(DisplayName = "Cohesion Test [Sql.Language] - MERGE: each WHEN retains its full original source span (#1715)")]
    public void Parse_WhenClauses_ShouldRetainSourceSpans()
    {
        // Arrange
        string[] clauses = ["WHEN MATCHED AND s.v>0 THEN UPDATE SET v=s.v",
            "WHEN MATCHED THEN DO NOTHING", "WHEN NOT MATCHED BY TARGET THEN INSERT(id,v) VALUES(s.id,s.v)",
            "WHEN NOT MATCHED BY SOURCE AND t.v>0 THEN UPDATE SET v=0", "WHEN NOT MATCHED BY SOURCE THEN DELETE"];
        string sql = "MERGE INTO t USING s ON t.id=s.id\n  " + string.Join(" /* next */\n  ", clauses) + " RETURNING t.id;";

        // Act
        var statement = (SqlQueryStatement)new SqlQueryParser().Parse(sql);
        var merge = statement.SqlExpression.ShouldBeOfType<SqlMergeExpression>();

        // Assert
        statement.Diagnostics.ShouldNotContain(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        merge.Actions.Select(clause => sql[clause.Location.Start..clause.Location.End]).ShouldBe(clauses);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - MERGE: complete action grammar round trips (#1715)")]
    [InlineData("MERGE INTO dbo.t AS a USING s AS b ON a.id=b.id WHEN MATCHED AND b.v>0 THEN UPDATE SET v=b.v, id=DEFAULT WHEN MATCHED THEN DELETE WHEN NOT MATCHED BY TARGET AND b.id>0 THEN INSERT (id,v) VALUES(b.id,DEFAULT) WHEN NOT MATCHED THEN DO NOTHING WHEN NOT MATCHED BY SOURCE AND a.id>1 THEN UPDATE SET v=0 WHEN NOT MATCHED BY SOURCE THEN DELETE RETURNING merge_action() AS action,a.id,b.v")]
    [InlineData("merge into t a using (select id, v from s) b on a.id=b.id when matched then do nothing when not matched by source then do nothing when not matched then insert default values returning *")]
    [InlineData("MERGE INTO \"when\" USING (s FULL JOIN u USING (id)) ON TRUE WHEN NOT MATCHED BY TARGET THEN INSERT (\"merge_action\") VALUES (1)")]
    [InlineData("MERGE INTO t USING s LEFT JOIN u ON s.id=u.id ON t.id=s.id WHEN MATCHED THEN UPDATE SET v=CASE WHEN u.id IS NULL THEN 1 ELSE 2 END RETURNING UPPER(merge_action())")]
    [InlineData("MERGE INTO t USING (s AS old JOIN u AS new ON old.id=new.id) ON t.id=old.id WHEN MATCHED THEN DELETE RETURNING old.id,new.id")]
    public void Parse_CompleteGrammar_ShouldRoundTrip(string sql)
    {
        // Arrange / Act
        var first = (SqlQueryStatement)new SqlQueryParser().Parse(sql);
        var merge = first.SqlExpression.ShouldBeOfType<SqlMergeExpression>();
        string rendered = SqlExpressionRenderer.Render(merge);
        var second = (SqlQueryStatement)new SqlQueryParser().Parse(rendered);

        // Assert
        first.Diagnostics.ShouldNotContain(d => d.Severity == DiagnosticSeverity.Error);
        second.Diagnostics.ShouldNotContain(d => d.Severity == DiagnosticSeverity.Error);
        SqlExpressionRenderer.Render(second.SqlExpression.ShouldBeOfType<SqlMergeExpression>()).ShouldBe(rendered);
        second.SqlExpression.CommandType.ShouldBe(SqlQueryCommandType.Merge);
        var actual = (SqlMergeExpression)second.SqlExpression;
        actual.Table.TableName.ShouldBe(merge.Table.TableName);
        actual.Table.SchemaName.ShouldBe(merge.Table.SchemaName);
        actual.Table.Alias.ShouldBe(merge.Table.Alias);
        SqlTreeDump.Dump(actual.Condition).ShouldBe(SqlTreeDump.Dump(merge.Condition));
        actual.Actions.Count.ShouldBe(merge.Actions.Count);
        for (int i = 0; i < actual.Actions.Count; i++)
        {
            var expectedAction = merge.Actions[i];
            var actualAction = actual.Actions[i];
            actualAction.MatchKind.ShouldBe(expectedAction.MatchKind);
            actualAction.Action.ShouldBe(expectedAction.Action);
            actualAction.IsDefaultValues.ShouldBe(expectedAction.IsDefaultValues);
            actualAction.IsByTargetExplicit.ShouldBe(expectedAction.IsByTargetExplicit);
            actualAction.Columns.ShouldBe(expectedAction.Columns);
            SqlTreeDump.Dump(actualAction.Condition).ShouldBe(SqlTreeDump.Dump(expectedAction.Condition));
            actualAction.Values?.Select(SqlTreeDump.Dump).ShouldBe(expectedAction.Values?.Select(SqlTreeDump.Dump));
            actualAction.Assignments.Select(value => (value.ColumnName, SqlTreeDump.Dump(value.Value)))
                .ShouldBe(expectedAction.Assignments.Select(value => (value.ColumnName, SqlTreeDump.Dump(value.Value))));
        }
        actual.Returning?.Select(column => (column.Alias, SqlTreeDump.Dump(column.Expression)))
            .ShouldBe(merge.Returning?.Select(column => (column.Alias, SqlTreeDump.Dump(column.Expression))));
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - MERGE: malformed actions cannot drop written syntax (#1715)")]
    [InlineData("MERGE t USING s ON TRUE WHEN MATCHED THEN DELETE")]
    [InlineData("MERGE INTO t USING s ON TRUE")]
    [InlineData("MERGE INTO t USING s ON WHEN MATCHED THEN DELETE")]
    [InlineData("MERGE INTO t USING s ON TRUE WHEN MATCHED INSERT VALUES(1)")]
    [InlineData("MERGE INTO t USING s ON TRUE WHEN MATCHED THEN INSERT VALUES(1)")]
    [InlineData("MERGE INTO t USING s ON TRUE WHEN NOT MATCHED THEN DELETE")]
    [InlineData("MERGE INTO t USING s ON TRUE WHEN NOT MATCHED BY SOURCE THEN INSERT VALUES(1)")]
    [InlineData("MERGE INTO t USING s ON TRUE WHEN MATCHED THEN UPDATE SET")]
    [InlineData("MERGE INTO t USING s ON TRUE WHEN MATCHED THEN UPDATE SET v=1,")]
    [InlineData("MERGE INTO t USING s ON TRUE WHEN MATCHED THEN DO")]
    [InlineData("MERGE INTO t USING s ON TRUE WHEN NOT MATCHED BY potato THEN DO NOTHING")]
    [InlineData("MERGE INTO t USING s ON TRUE WHEN NOT MATCHED THEN INSERT VALUES(1),(2)")]
    [InlineData("MERGE INTO t USING s ON TRUE WHEN NOT MATCHED THEN INSERT VALUES()")]
    [InlineData("MERGE INTO t USING s ON TRUE WHEN MATCHED THEN DELETE WHERE TRUE")]
    [InlineData("MERGE INTO t USING s ON TRUE WHEN MATCHED THEN DELETE RETURNING")]
    public void Parse_MalformedMerge_ShouldReportSyntax(string sql)
    {
        // Arrange / Act
        var statement = Should.NotThrow(() => new SqlQueryParser().Parse(sql));

        // Assert
        statement.Diagnostics.ShouldContain(d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Language] - MERGE: EXPLAIN retains the complete statement (#1715)")]
    public void Parse_ExplainMerge_ShouldRetainStatement()
    {
        // Arrange / Act
        var statement = (SqlQueryStatement)new SqlQueryParser().Parse("EXPLAIN MERGE INTO t USING s ON t.id=s.id WHEN MATCHED THEN DELETE;");

        // Assert
        statement.Diagnostics.ShouldNotContain(d => d.Severity == DiagnosticSeverity.Error);
        statement.SqlExpression.ShouldBeOfType<SqlExplainExpression>().Statement.ShouldBeOfType<SqlMergeExpression>();
    }
}
