using System.Linq;

using Assimalign.Cohesion.Database.Sql.Language.Internal;
using Assimalign.Cohesion.Database.Types;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Language.Tests;

public sealed class SqlUnsupportedFunctionTests
{
    [Fact(DisplayName = "Cohesion Test [Sql.Language] - Functions: every recognized unsupported call reports one named diagnostic")]
    public void Parse_RecognizedUnsupportedFunctions_ShouldReportSingleCapabilityError()
    {
        // Arrange
        var entries = SqlUnsupportedVocabulary.Words.Where(word => word.Position is
            SqlWordPosition.Function or SqlWordPosition.NiladicFunction or SqlWordPosition.WindowFunction);
        // Act / Assert
        foreach (var entry in entries)
        {
            var statement = new SqlQueryParser().Parse($"SELECT {entry.Spelling}(a) FROM t;");
            var diagnostic = statement.Diagnostics.ShouldHaveSingleItem();
            diagnostic.Code.ShouldBe("COHDBL001");
            diagnostic.Message.ShouldNotBeNull().ShouldContain(entry.Spelling, Case.Sensitive);
            diagnostic.Start.ShouldBe(7);
            diagnostic.End.ShouldBe(7 + entry.Spelling.Length);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Functions: niladic datetime forms reject while quoted columns parse")]
    [InlineData("CURRENT_DATE")]
    [InlineData("CURRENT_TIME")]
    [InlineData("CURRENT_TIMESTAMP")]
    [InlineData("LOCALTIME")]
    [InlineData("LOCALTIMESTAMP")]
    public void Parse_NiladicDatetime_ShouldRequireQuotedColumnName(string name)
    {
        // Arrange / Act / Assert
        foreach (string expression in new[] { name, name + "()" })
        {
            var diagnostic = new SqlQueryParser().Parse($"SELECT {expression} FROM t;").Diagnostics.ShouldHaveSingleItem();
            diagnostic.Code.ShouldBe("COHDBL001");
            diagnostic.Severity.ShouldBe(DiagnosticSeverity.Error);
            diagnostic.Message.ShouldNotBeNull().ShouldContain(name, Case.Sensitive);
        }
        new SqlQueryParser().Parse($"SELECT \"{name}\" FROM t;").Diagnostics.ShouldBeEmpty();
    }
}
