using System;
using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Language.Tests;

public sealed class SqlAlterTableParserTests
{
    [Theory(DisplayName = "Cohesion Test [Sql.Language] - ALTER: each complete action retains its typed payload")]
    [InlineData("RENAME TO next", nameof(SqlAlterRenameTableAction))]
    [InlineData("RENAME COLUMN \"old name\" TO \"new name\"", nameof(SqlAlterRenameColumnAction))]
    [InlineData("RENAME \"old name\" TO \"new name\"", nameof(SqlAlterRenameColumnAction))]
    [InlineData("ALTER COLUMN \"old name\" SET DEFAULT -7", nameof(SqlAlterSetDefaultAction))]
    [InlineData("ALTER \"old name\" SET DEFAULT DATE '2024-01-01'", nameof(SqlAlterSetDefaultAction))]
    [InlineData("ALTER COLUMN \"old name\" DROP DEFAULT", nameof(SqlAlterDropDefaultAction))]
    [InlineData("ALTER \"old name\" DROP DEFAULT", nameof(SqlAlterDropDefaultAction))]
    [InlineData("ALTER COLUMN \"old name\" SET NOT NULL", nameof(SqlAlterSetNotNullAction))]
    [InlineData("ALTER \"old name\" SET NOT NULL", nameof(SqlAlterSetNotNullAction))]
    [InlineData("ALTER COLUMN \"old name\" DROP NOT NULL", nameof(SqlAlterDropNotNullAction))]
    [InlineData("ALTER \"old name\" DROP NOT NULL", nameof(SqlAlterDropNotNullAction))]
    public void Parse_CompleteAction_ShouldProduceTypedAction(string action, string type)
    {
        // Arrange / Act
        var statement = new SqlQueryParser().Parse($"ALTER TABLE \"my schema\".\"my table\" {action};");

        // Assert
        statement.Diagnostics.ShouldNotContain(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var alter = statement.ShouldBeOfType<SqlQueryStatement>().SqlExpression.ShouldBeOfType<SqlAlterTableExpression>();
        alter.Table.SchemaName.ShouldBe("my schema");
        alter.Table.TableName.ShouldBe("my table");
        alter.Action.GetType().Name.ShouldBe(type);
        if (alter.Action is SqlAlterRenameColumnAction rename)
        {
            rename.ColumnName.ShouldBe("old name");
            rename.NewName.ShouldBe("new name");
        }
        if (alter.Action is SqlAlterSetDefaultAction change)
        {
            change.ColumnName.ShouldBe("old name");
            SqlExpressionRenderer.Render(change.DefaultValue).ShouldBe(action.Contains("DATE", StringComparison.Ordinal) ? "DATE '2024-01-01'" : "-7");
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - ALTER: excluded actions carry a named capability diagnostic")]
    [InlineData("SET SCHEMA other", "SET SCHEMA")]
    [InlineData("RENAME CONSTRAINT old TO newer", "RENAME CONSTRAINT")]
    [InlineData("ALTER c SET DEFAULT 7, ALTER c SET NOT NULL", "comma-separated ALTER TABLE actions")]
    public void Parse_ExcludedAction_ShouldNameCapability(string action, string construct)
    {
        // Arrange / Act
        var statement = new SqlQueryParser().Parse("ALTER TABLE t " + action);

        // Assert
        var diagnostic = statement.Diagnostics.Single(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        diagnostic.Code.ShouldBe("CK00001");
        diagnostic.Message!.ShouldContain(construct, Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - ALTER TYPE: optional words, modifiers and USING round trip")]
    [InlineData("ALTER COLUMN c TYPE BIGINT")]
    [InlineData("ALTER c SET DATA TYPE NUMERIC(8,2) USING c + 1")]
    [InlineData("ALTER c TYPE VARCHAR(20) COLLATE binary USING CAST(c AS TEXT)")]
    [InlineData("ALTER c TYPE \"domain name\" USING CASE WHEN c IS NULL THEN 0 ELSE c END")]
    public void Parse_AlterType_ShouldRetainAndRenderAction(string action)
    {
        // Arrange / Act
        var statement = new SqlQueryParser().Parse("ALTER TABLE \"my schema\".t " + action);

        // Assert
        statement.Diagnostics.ShouldNotContain(d => d.Severity == DiagnosticSeverity.Error);
        var alter = statement.ShouldBeOfType<SqlQueryStatement>().SqlExpression.ShouldBeOfType<SqlAlterTableExpression>();
        var change = alter.Action.ShouldBeOfType<SqlAlterColumnTypeAction>();
        change.Column.ColumnName.ShouldBe("c");
        string rendered = SqlExpressionRenderer.Render(alter);
        var roundtrip = new SqlQueryParser().Parse(rendered);
        roundtrip.Diagnostics.ShouldNotContain(d => d.Severity == DiagnosticSeverity.Error);
        SqlExpressionRenderer.Render(roundtrip.ShouldBeOfType<SqlQueryStatement>().SqlExpression.ShouldBeOfType<SqlAlterTableExpression>()).ShouldBe(rendered);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - ALTER TYPE review: type names render with required quoting and retain existing delimiters")]
    [InlineData("a space", false)]
    [InlineData("a\"quote", false)]
    [InlineData("select", false)]
    [InlineData("a space", true)]
    [InlineData("a\"quote", true)]
    [InlineData("select", true)]
    [InlineData("\"odd\"", false)]
    [InlineData("\"odd\"", true)]
    public void Render_AlterTypeDomain_ShouldPreserveTypeName(string name, bool alreadyQuoted)
    {
        // Arrange
        string quoted = "\"" + name.Replace("\"", "\"\"") + "\"";
        var parsed = new SqlQueryParser().Parse("ALTER TABLE t ALTER v TYPE " + quoted);
        var original = parsed.ShouldBeOfType<SqlQueryStatement>().SqlExpression.ShouldBeOfType<SqlAlterTableExpression>();
        var alter = alreadyQuoted ? original : new SqlAlterTableExpression(original.Table,
            new SqlAlterColumnTypeAction(new SqlColumnDefinition("v", name, true, false, null), null), null, null);

        // Act
        string rendered = SqlExpressionRenderer.Render(alter);
        var reloaded = new SqlQueryParser().Parse(rendered);

        // Assert
        rendered.ShouldContain("TYPE " + quoted, Case.Sensitive);
        reloaded.Diagnostics.ShouldNotContain(d => d.Severity == DiagnosticSeverity.Error);
        reloaded.ShouldBeOfType<SqlQueryStatement>().SqlExpression.ShouldBeOfType<SqlAlterTableExpression>()
            .Action.ShouldBeOfType<SqlAlterColumnTypeAction>().Column.DataType.ShouldBe(quoted);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - ALTER TYPE: incomplete clauses are syntax errors")]
    [InlineData("ALTER c TYPE")]
    [InlineData("ALTER c SET DATA")]
    [InlineData("ALTER c TYPE INT USING")]
    [InlineData("ALTER c TYPE INT COLLATE")]
    [InlineData("ALTER c TYPE INT USING c +")]
    public void Parse_IncompleteAlterType_ShouldReportSyntax(string action)
    {
        // Arrange / Act
        var statement = new SqlQueryParser().Parse("ALTER TABLE t " + action);

        // Assert
        statement.Diagnostics.ShouldContain(d => d.Code == "CS00003");
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - ALTER: incomplete actions never execute a consumed prefix")]
    [InlineData("RENAME TO")]
    [InlineData("RENAME a b")]
    [InlineData("RENAME TO other.next")]
    [InlineData("ALTER c SET DEFAULT")]
    [InlineData("ALTER c")]
    [InlineData("ALTER COLUMN c")]
    [InlineData("ALTER c INT")]
    [InlineData("ALTER c SET")]
    [InlineData("ALTER c DROP")]
    [InlineData("ALTER COLUMN c SET")]
    [InlineData("ALTER COLUMN c DROP")]
    [InlineData("ALTER CONSTRAINT fk DEFERRABLE")]
    [InlineData("SET")]
    [InlineData("SET TABLESPACE x")]
    [InlineData("SET LOGGED")]
    [InlineData("SET bananas")]
    [InlineData("ALTER c SET NOT")]
    [InlineData("ALTER c DROP NOT")]
    public void Parse_IncompleteAction_ShouldReportSyntax(string action)
    {
        // Arrange / Act
        var statement = new SqlQueryParser().Parse("ALTER TABLE t " + action);

        // Assert
        statement.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS00003");
        statement.Diagnostics.ShouldNotContain(diagnostic => diagnostic.Code == "CK00001");
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - ALTER: syntax diagnostics list every supported action family")]
    [InlineData("")]
    [InlineData("MODIFY c INT")]
    public void Parse_UnknownOrMissingAction_ShouldListSupportedGrammar(string action)
    {
        // Arrange / Act
        var statement = new SqlQueryParser().Parse("ALTER TABLE t " + action);

        // Assert
        var diagnostic = statement.Diagnostics.Single(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        diagnostic.Code.ShouldBe("CS00003");
        foreach (string supported in new[] { "ADD [COLUMN]", "ADD CONSTRAINT", "DROP [COLUMN]", "DROP CONSTRAINT", "RENAME TO", "RENAME [COLUMN]", "ALTER COLUMN", "SET/DROP DEFAULT", "SET/DROP NOT NULL" })
        {
            diagnostic.Message!.ShouldContain(supported, Case.Sensitive);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - ALTER: RENAME and TO remain contextual identifiers")]
    [InlineData("CREATE TABLE rename (to INT)")]
    [InlineData("SELECT to FROM rename")]
    [InlineData("CREATE TABLE to (rename INT)")]
    [InlineData("SELECT rename FROM to")]
    public void Parse_ContextualNames_ShouldRemainIdentifiers(string sql)
    {
        // Arrange / Act
        var statement = new SqlQueryParser().Parse(sql);

        // Assert
        statement.Diagnostics.ShouldNotContain(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }
}
