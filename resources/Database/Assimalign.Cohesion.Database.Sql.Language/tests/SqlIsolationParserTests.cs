using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Language.Tests;

public sealed class SqlIsolationParserTests
{
    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Isolation: every spelling roundtrips all levels and mode separators")]
    [InlineData("BEGIN")]
    [InlineData("BEGIN TRANSACTION")]
    [InlineData("BEGIN WORK")]
    [InlineData("START TRANSACTION")]
    [InlineData("SET TRANSACTION")]
    public void Parse_IsolationModes_ShouldRoundtrip(string prefix)
    {
        // Arrange / Act / Assert
        foreach (string level in new[] { "READ COMMITTED", "READ UNCOMMITTED", "REPEATABLE READ", "SERIALIZABLE" })
        {
            foreach (string separator in new[] { ", ", " " })
            {
                foreach (string terminator in new[] { "", ";" })
                {
                    string sql = prefix + " ISOLATION LEVEL " + level + separator + "ISOLATION LEVEL " + level;
                    var parsed = new SqlQueryParser().Parse(sql.ToLowerInvariant().Replace(" ", " /* trivia */ ") + terminator)
                        .ShouldBeOfType<SqlQueryStatement>();
                    parsed.Diagnostics.ShouldNotContain(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
                    var control = parsed.SqlExpression.ShouldBeOfType<SqlTransactionExpression>();
                    control.IsolationLevels.Count.ShouldBe(2);
                    var canonical = SqlExpressionRenderer.Render(control);
                    var roundtrip = new SqlQueryParser().Parse(canonical + ";").ShouldBeOfType<SqlQueryStatement>();
                    roundtrip.Diagnostics.ShouldBeEmpty();
                    roundtrip.SqlExpression.ShouldBeOfType<SqlTransactionExpression>().IsolationLevels.ShouldBe(control.IsolationLevels);
                }
            }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Isolation: malformed modes carry a located syntax diagnostic")]
    [InlineData("BEGIN ISOLATION")]
    [InlineData("BEGIN ISOLATION LEVEL")]
    [InlineData("BEGIN ISOLATION LEVEL REPEATABLE")]
    [InlineData("BEGIN ISOLATION LEVEL READ")]
    [InlineData("BEGIN ISOLATION LEVEL READ ONLY")]
    [InlineData("BEGIN ISOLATION LEVEL SNAPSHOT")]
    [InlineData("SET TRANSACTION")]
    [InlineData("SET TRANSACTION ISOLATION LEVEL READ COMMITTED,")]
    [InlineData("BEGIN ISOLATION LEVEL READ COMMITTED,, ISOLATION LEVEL REPEATABLE READ")]
    public void Parse_MalformedIsolation_ShouldRefuse(string sql)
    {
        // Arrange / Act
        var parsed = new SqlQueryParser().Parse(sql + ";").ShouldBeOfType<SqlQueryStatement>();
        // Assert
        parsed.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ShouldHaveSingleItem().Code.ShouldBe("CS00012");
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Isolation: other transaction modes name the unsupported form")]
    [InlineData("READ ONLY")]
    [InlineData("READ WRITE")]
    [InlineData("DEFERRABLE")]
    [InlineData("NOT DEFERRABLE")]
    public void Parse_OtherTransactionModes_ShouldNameForm(string mode)
    {
        // Arrange / Act / Assert
        foreach (string prefix in new[] { "BEGIN", "START TRANSACTION", "SET TRANSACTION", "BEGIN ISOLATION LEVEL READ COMMITTED," })
        {
            var parsed = new SqlQueryParser().Parse(prefix + " " + mode + ";").ShouldBeOfType<SqlQueryStatement>();
            var diagnostic = parsed.Diagnostics.ShouldHaveSingleItem();
            diagnostic.Code.ShouldBe("CK00001");
            diagnostic.Message!.ShouldContain(mode, Case.Sensitive);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Language] - Isolation: mode words remain contextual identifiers")]
    public void Parse_ModeWordsAsIdentifiers_ShouldRemainValid()
    {
        // Arrange / Act
        var parsed = new SqlQueryParser().Parse("SELECT isolation, level, committed, uncommitted, repeatable, serializable, deferrable FROM modes;")
            .ShouldBeOfType<SqlQueryStatement>();
        // Assert
        parsed.Diagnostics.ShouldBeEmpty();
    }
}
