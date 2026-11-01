using System.Collections.Generic;
using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Language.Tests;

public sealed class SqlOnUpdateParserTests
{
    public static IEnumerable<object[]> Actions()
    {
        string[] actions = ["RESTRICT", "CASCADE", "NO ACTION", "SET NULL", "SET DEFAULT"];
        for (int update = 0; update < actions.Length; update++)
        {
            for (int delete = 0; delete < actions.Length; delete++)
            {
                for (int shape = 0; shape < 3; shape++)
                {
                    yield return [actions[update], (SqlReferentialAction)update, actions[delete], (SqlReferentialAction)delete, shape, false];
                    yield return [actions[update], (SqlReferentialAction)update, actions[delete], (SqlReferentialAction)delete, shape, true];
                }
            }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - ON UPDATE: every action, ordering and constraint shape round trips (#1723)")]
    [MemberData(nameof(Actions))]
    public void Render_OnUpdateActions_ShouldRoundTrip(string update, SqlReferentialAction expectedUpdate,
        string delete, SqlReferentialAction expectedDelete, int shape, bool updateFirst)
    {
        // Arrange
        string actions = updateFirst ? $"ON UPDATE {update} ON DELETE {delete}" : $"ON DELETE {delete} ON UPDATE {update}";
        string reference = $"""REFERENCES "parent table"("key") {actions}""";
        string sql = shape switch
        {
            0 => $"CREATE TABLE c (pid INT CONSTRAINT fk {reference});",
            1 => $"CREATE TABLE c (pid INT, CONSTRAINT fk FOREIGN KEY(pid) {reference});",
            _ => $"ALTER TABLE c ADD CONSTRAINT fk FOREIGN KEY(pid) {reference};",
        };
        var parsed = Parse(sql);
        parsed.Diagnostics.ShouldBeEmpty();
        var constraint = Constraint(parsed);

        // Act
        string rendered = SqlExpressionRenderer.Render(constraint);
        var reparsed = Parse("ALTER TABLE c ADD " + rendered + ";");

        // Assert
        constraint.OnUpdate.ShouldBe(expectedUpdate);
        constraint.OnDelete.ShouldBe(expectedDelete);
        reparsed.Diagnostics.ShouldBeEmpty();
        Constraint(reparsed).OnUpdate.ShouldBe(expectedUpdate);
        Constraint(reparsed).OnDelete.ShouldBe(expectedDelete);
        SqlExpressionRenderer.Render(Constraint(reparsed)).ShouldBe(rendered);
        SqlExpressionRenderer.Render(reparsed.SqlExpression.ShouldBeOfType<SqlAlterTableExpression>())
            .ShouldBe("ALTER TABLE c ADD " + rendered);
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Language] - ON UPDATE: omitted actions default independently to NO ACTION (#1723)")]
    public void Parse_OmittedActions_ShouldDefaultToNoAction()
    {
        // Arrange / Act
        var plain = Constraint(Parse("CREATE TABLE c(pid INT REFERENCES p(id));"));
        var deletion = Constraint(Parse("CREATE TABLE c(pid INT REFERENCES p(id) ON DELETE CASCADE);"));
        var update = Constraint(Parse("CREATE TABLE c(pid INT REFERENCES p(id) ON UPDATE RESTRICT);"));

        // Assert
        plain.OnUpdate.ShouldBe(SqlReferentialAction.NoAction);
        deletion.OnUpdate.ShouldBe(SqlReferentialAction.NoAction);
        update.OnDelete.ShouldBe(SqlReferentialAction.NoAction);
        SqlLanguageProfile.Instance.Supports(SqlClauses.OnUpdate).ShouldBeTrue();
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - ON UPDATE: malformed and duplicate actions fail parsing (#1723)")]
    [InlineData("ON UPDATE")]
    [InlineData("ON UPDATE NO")]
    [InlineData("ON UPDATE SET")]
    [InlineData("ON UPDATE BOGUS")]
    [InlineData("ON UPDATE CASCADE ON UPDATE RESTRICT")]
    [InlineData("ON DELETE CASCADE ON DELETE RESTRICT")]
    [InlineData("ON UPDATE CASCADE ON DELETE NO ACTION ON UPDATE SET NULL")]
    public void Parse_InvalidAction_ShouldReportSyntax(string actions)
    {
        // Arrange / Act
        var parsed = Parse($"CREATE TABLE c(pid INT REFERENCES p(id) {actions});");

        // Assert
        parsed.Diagnostics.ShouldContain(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error && diagnostic.Code == "CS00003");
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - ON UPDATE: action column lists remain unsupported (#1723)")]
    [InlineData("SET NULL")]
    [InlineData("SET DEFAULT")]
    public void Parse_ActionColumnList_ShouldReportUnsupported(string action)
    {
        // Arrange / Act
        var parsed = Parse($"CREATE TABLE c(pid INT REFERENCES p(id) ON UPDATE {action}(pid));");

        // Assert
        parsed.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CK00001");
    }

    private static SqlQueryStatement Parse(string sql) => (SqlQueryStatement)new SqlQueryParser().Parse(sql);

    private static SqlConstraintDefinition Constraint(SqlQueryStatement statement) => statement.SqlExpression switch
    {
        SqlCreateTableExpression create => create.Constraints.Single(),
        SqlAlterTableExpression alter => alter.Action.ShouldBeOfType<SqlAlterAddConstraintAction>().Constraint,
        _ => throw new System.InvalidOperationException(),
    };
}
