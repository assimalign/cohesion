using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Documents.Language.Tests;

/// <summary>
/// A <c>--</c> comment ends at the first line terminator: LF, CR, NEL, LS or PS. The shared
/// lexer used to end it only at LF, so a clause after a lone CR was comment text and
/// <c>SELECT * FROM people -- c&lt;CR&gt;WHERE age &gt; 1</c> returned every document (#1150).
/// Diagnostic lines break at the same terminators.
/// </summary>
public sealed class OqlLineCommentTests
{
    /// <summary>The WHERE after the comment stays in effect.</summary>
    /// <param name="terminator">The line terminator, by name (see <see cref="Terminator"/>).</param>
    [Theory(DisplayName = "Cohesion Test [Documents.Language] - Comments: a WHERE after a line comment stays in effect at every terminator")]
    [InlineData("LF")]
    [InlineData("CR")]
    [InlineData("CRLF")]
    [InlineData("NEL")]
    [InlineData("LS")]
    [InlineData("PS")]
    public void Parse_WhereAfterLineComment_ShouldKeepThePredicate(string terminator)
    {
        // Arrange
        string oql = "SELECT * FROM people -- note" + Terminator(terminator) + "WHERE age > 1";

        // Act
        var statement = Parse(oql);

        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        var select = statement.OqlExpression.ShouldBeOfType<OqlSelectExpression>();
        select.Collection.ShouldBe("people");
        select.Predicate.ShouldBeOfType<OqlBinaryExpression>().Operator.ShouldBe(">");
    }

    /// <summary>A clause that follows a comment on its own line is part of the statement.</summary>
    /// <param name="terminator">The line terminator, by name.</param>
    [Theory(DisplayName = "Cohesion Test [Documents.Language] - Comments: FROM after a line comment is parsed at every terminator")]
    [InlineData("LF")]
    [InlineData("CR")]
    [InlineData("CRLF")]
    [InlineData("NEL")]
    [InlineData("LS")]
    [InlineData("PS")]
    public void Parse_FromAfterLineComment_ShouldParse(string terminator)
    {
        // Arrange
        string oql = "SELECT name -- the projected field" + Terminator(terminator) + "FROM people;";

        // Act
        var statement = Parse(oql);

        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        var select = statement.OqlExpression.ShouldBeOfType<OqlSelectExpression>();
        select.Collection.ShouldBe("people");
        select.Projections.ShouldHaveSingleItem();
    }

    /// <summary>
    /// Text on the comment's own line stays comment text, so a FROM written there leaves the
    /// statement incomplete rather than silently changing it.
    /// </summary>
    /// <param name="terminator">The line terminator, by name.</param>
    [Theory(DisplayName = "Cohesion Test [Documents.Language] - Comments: text on the comment's own line stays comment text")]
    [InlineData("LF")]
    [InlineData("CR")]
    [InlineData("NEL")]
    [InlineData("LS")]
    [InlineData("PS")]
    public void Parse_TextOnTheCommentLine_ShouldStayInTheComment(string terminator)
    {
        // Arrange
        string oql = "SELECT name -- FROM people" + Terminator(terminator);

        // Act
        var statement = Parse(oql);

        // Assert
        statement.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("OQL0002");
    }

    /// <summary>
    /// Diagnostic lines break where the lexer ends a comment: every terminator starts a line, and
    /// CR LF starts one line, not two.
    /// </summary>
    /// <param name="terminator">The line terminator, by name.</param>
    [Theory(DisplayName = "Cohesion Test [Documents.Language] - Comments: diagnostic lines break at every terminator, CR LF once")]
    [InlineData("LF")]
    [InlineData("CR")]
    [InlineData("CRLF")]
    [InlineData("NEL")]
    [InlineData("LS")]
    [InlineData("PS")]
    public void Parse_DiagnosticAfterLineComment_ShouldReportTheNextLine(string terminator)
    {
        // Arrange
        string separator = Terminator(terminator);
        string oql = "SELECT * -- note" + separator + "FROM people" + separator + "LIMIT 10";

        // Act
        var diagnostic = Parse(oql).Diagnostics.ShouldHaveSingleItem();

        // Assert
        diagnostic.Code.ShouldBe("COHDBL001");
        diagnostic.Start.ShouldBe(oql.IndexOf("LIMIT", StringComparison.Ordinal));
        diagnostic.Line.ShouldBe(3);
    }

    /// <summary>A string literal that spans a terminator ends its expression on the next line.</summary>
    /// <param name="terminator">The line terminator, by name.</param>
    [Theory(DisplayName = "Cohesion Test [Documents.Language] - Comments: an expression ending in a multi-line literal ends on the next line")]
    [InlineData("LF")]
    [InlineData("CR")]
    [InlineData("CRLF")]
    [InlineData("NEL")]
    [InlineData("LS")]
    [InlineData("PS")]
    public void Parse_PredicateEndingInMultiLineLiteral_ShouldEndOnTheNextLine(string terminator)
    {
        // Arrange
        string oql = "SELECT * FROM people WHERE name = 'x" + Terminator(terminator) + "y'";

        // Act
        var statement = Parse(oql);

        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        var predicate = statement.OqlExpression.ShouldBeOfType<OqlSelectExpression>().Predicate.ShouldNotBeNull();
        var location = predicate.Location.ShouldNotBeNull();
        location.StartLine.ShouldBe(1);
        location.EndLine.ShouldBe(2);
    }

    /// <summary>The line terminators by name, so no invisible character sits in the test source.</summary>
    /// <param name="name">LF, CR, CRLF, NEL, LS or PS.</param>
    /// <returns>The terminator text.</returns>
    private static string Terminator(string name) => name switch
    {
        "LF" => "\n",
        "CR" => "\r",
        "CRLF" => "\r\n",
        "NEL" => ((char)0x0085).ToString(),
        "LS" => ((char)0x2028).ToString(),
        "PS" => ((char)0x2029).ToString(),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown line terminator."),
    };

    private static OqlQueryStatement Parse(string oql) => new OqlQueryParser().Parse(oql).ShouldBeOfType<OqlQueryStatement>();
}
