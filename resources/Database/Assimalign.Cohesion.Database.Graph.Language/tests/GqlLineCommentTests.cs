using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Graph.Language.Tests;

/// <summary>
/// A <c>--</c> comment, ISO/IEC 39075's <c>&lt;simple comment&gt;</c>, ends at the first line
/// terminator: LF, CR, NEL, LS or PS. The shared lexer used to end it only at LF, so a clause
/// after a lone CR was comment text and <c>MATCH (a) -- c&lt;CR&gt;WHERE ... DETACH DELETE a</c>
/// deleted every node (#1150). Diagnostic lines break at the same terminators. Nothing here asserts
/// on <c>--</c> directly after <c>)</c> or <c>]</c>, which gql-label-direction (#1139) owns.
/// </summary>
public sealed class GqlLineCommentTests
{
    /// <summary>The WHERE after the comment stays in effect, for a read and for a delete.</summary>
    /// <param name="terminator">The line terminator, by name (see <see cref="Terminator"/>).</param>
    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Comments: a WHERE after a line comment stays in effect at every terminator")]
    [InlineData("LF")]
    [InlineData("CR")]
    [InlineData("CRLF")]
    [InlineData("NEL")]
    [InlineData("LS")]
    [InlineData("PS")]
    public void Parse_WhereAfterLineComment_ShouldKeepThePredicate(string terminator)
    {
        // Arrange
        string separator = Terminator(terminator);
        string read = "MATCH (a:Person) -- note" + separator + "WHERE a.age > 1 RETURN a";
        string delete = "MATCH (a:Person) -- note" + separator + "WHERE a.name = 'x' DETACH DELETE a";

        // Act
        var readStatement = Parse(read);
        var deleteStatement = Parse(delete);

        // Assert
        readStatement.Diagnostics.ShouldBeEmpty();
        var comparison = readStatement.GqlExpression.Predicate.ShouldBeOfType<GqlBinaryExpression>();
        comparison.Operator.ShouldBe(">");
        comparison.Left.ShouldBeOfType<GqlPropertyExpression>().Property.ShouldBe("age");
        readStatement.GqlExpression.Projections.ShouldHaveSingleItem();

        deleteStatement.Diagnostics.ShouldBeEmpty();
        deleteStatement.GqlExpression.Predicate.ShouldBeOfType<GqlBinaryExpression>().Operator.ShouldBe("=");
        deleteStatement.GqlExpression.DetachDelete.ShouldBeTrue();
        deleteStatement.GqlExpression.DeleteVariables.ShouldBe(["a"]);
    }

    /// <summary>
    /// A comment separated from the pattern by whitespace is an ISO comment wherever it ends, so
    /// the clause on the next line is parsed. This is the case #1139 keeps unchanged.
    /// </summary>
    /// <param name="terminator">The line terminator, by name.</param>
    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Comments: a clause after a spaced line comment is parsed at every terminator")]
    [InlineData("LF")]
    [InlineData("CR")]
    [InlineData("CRLF")]
    [InlineData("NEL")]
    [InlineData("LS")]
    [InlineData("PS")]
    public void Parse_ClauseAfterSpacedLineComment_ShouldParse(string terminator)
    {
        // Arrange
        string gql = "MATCH (a) -- comment" + Terminator(terminator) + "RETURN a";

        // Act
        var statement = Parse(gql);

        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        statement.GqlExpression.Matches.ShouldHaveSingleItem();
        statement.GqlExpression.Projections.ShouldHaveSingleItem();
    }

    /// <summary>
    /// A comment that runs to the end of the text still hides what follows on its own line, so
    /// a statement whose RETURN is on the comment's line is incomplete rather than silently cut.
    /// </summary>
    /// <param name="terminator">The line terminator, by name.</param>
    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Comments: text on the comment's own line stays comment text")]
    [InlineData("LF")]
    [InlineData("CR")]
    [InlineData("NEL")]
    [InlineData("LS")]
    [InlineData("PS")]
    public void Parse_TextOnTheCommentLine_ShouldStayInTheComment(string terminator)
    {
        // Arrange
        string gql = "MATCH (a) -- RETURN a" + Terminator(terminator);

        // Act
        var statement = Parse(gql);

        // Assert
        statement.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("GQL0002");
        statement.GqlExpression.Projections.ShouldBeEmpty();
    }

    /// <summary>
    /// Diagnostic lines break where the lexer ends a comment: every terminator starts a line, and
    /// CR LF starts one line, not two.
    /// </summary>
    /// <param name="terminator">The line terminator, by name.</param>
    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Comments: diagnostic lines break at every terminator, CR LF once")]
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
        string gql = "MATCH (a) -- note" + separator + "RETURN a" + separator + "LIMIT 2";

        // Act
        var diagnostic = Parse(gql).Diagnostics.ShouldHaveSingleItem();

        // Assert
        diagnostic.Code.ShouldBe("COHDBL001");
        diagnostic.Start.ShouldBe(gql.IndexOf("LIMIT", StringComparison.Ordinal));
        diagnostic.Line.ShouldBe(3);
    }

    /// <summary>A string literal that spans a terminator ends its expression on the next line.</summary>
    /// <param name="terminator">The line terminator, by name.</param>
    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Comments: an expression ending in a multi-line literal ends on the next line")]
    [InlineData("LF")]
    [InlineData("CR")]
    [InlineData("CRLF")]
    [InlineData("NEL")]
    [InlineData("LS")]
    [InlineData("PS")]
    public void Parse_PredicateEndingInMultiLineLiteral_ShouldEndOnTheNextLine(string terminator)
    {
        // Arrange
        string gql = "MATCH (a) WHERE a.name = 'x" + Terminator(terminator) + "y' RETURN a";

        // Act
        var statement = Parse(gql);

        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        var location = statement.GqlExpression.Predicate.ShouldNotBeNull().Location.ShouldNotBeNull();
        location.StartLine.ShouldBe(1);
        location.EndLine.ShouldBe(2);
    }

    /// <summary>
    /// ISO/IEC 39075 also spells a simple comment <c>//</c>, but the shared lexer reads <c>/</c> as
    /// the division operator in every language and has no per-language comment switch. Until an
    /// item adds the <c>//</c> form, with the same terminators, it fails loudly instead of being
    /// read as a comment or as an operator.
    /// </summary>
    /// <param name="terminator">The line terminator, by name.</param>
    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Comments: // is not a comment and fails loudly")]
    [InlineData("LF")]
    [InlineData("CR")]
    public void Parse_DoubleSolidus_ShouldReportSyntaxError(string terminator)
    {
        // Arrange
        string gql = "MATCH (a) // note" + Terminator(terminator) + "RETURN a";

        // Act
        var statement = Parse(gql);

        // Assert
        var error = statement.Diagnostics.ShouldHaveSingleItem();
        error.Code.ShouldBe("GQL0002");
        error.Start.ShouldBe(gql.IndexOf('/'));
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

    private static GqlQueryStatement Parse(string gql) => (GqlQueryStatement)new GqlQueryParser().Parse(gql);
}
