using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Graph.Language.Tests;

/// <summary>
/// Cypher arrows fail loudly (#1139, R10). ISO/IEC 39075 reads <c>--</c> as a simple comment, and
/// the shared lexer keeps that reading in every language, so <c>(a)-->(b)</c> is <c>(a)</c>
/// followed by a comment that runs to the next line terminator. A <c>--</c> comment that begins
/// exactly where a node's <c>)</c> or an edge's <c>]</c> ends is <c>GQL0008</c>, reported at the
/// <c>--</c>; whichever terminator ends the comment (#1150), the statement no longer truncates.
/// Under D3, <c>--&gt;</c>, <c>&lt;--</c>, <c>--</c> and <c>&lt;--&gt;</c> are never edges.
/// </summary>
public sealed class GqlCypherArrowTests
{
    /// <param name="terminator">The line terminator, by name (see <see cref="Terminator"/>).</param>
    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Cypher arrows: --> after a node is one GQL0008 at the -- at every terminator")]
    [InlineData("LF")]
    [InlineData("CR")]
    [InlineData("CRLF")]
    [InlineData("NEL")]
    [InlineData("LS")]
    [InlineData("PS")]
    public void Parse_CypherArrowBeforeLineBreak_ShouldReportGql0008(string terminator)
    {
        // Arrange
        string read = "MATCH (b:Person)-->(a)" + Terminator(terminator) + "RETURN b";
        string delete = "MATCH (a)-->(b)" + Terminator(terminator) + "DETACH DELETE a";

        // Act
        var readStatement = Parse(read);
        var deleteStatement = Parse(delete);

        // Assert
        AssertGql0008(read, readStatement, line: 1);
        AssertGql0008(delete, deleteStatement, line: 1);
        deleteStatement.GqlExpression.DeleteVariables.ShouldBeEmpty();
        deleteStatement.GqlExpression.DetachDelete.ShouldBeFalse();
    }

    /// <param name="gql">A statement that writes a Cypher arrow after a pattern element.</param>
    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Cypher arrows: --, -->, ]-- and ]--> after a pattern element are GQL0008")]
    [InlineData("MATCH (a)--(b) RETURN a")]
    [InlineData("MATCH (a)-[r]--(b) RETURN a")]
    [InlineData("MATCH (a)-[r]-->(b) RETURN a")]
    [InlineData("MATCH (a)<-[r]--(b) RETURN a")]
    [InlineData("MATCH (a)-[r]->(b)-- note\nRETURN a")]
    [InlineData("MATCH (a:Person {name: 'x'})-->(b) RETURN a")]
    [InlineData("MATCH p = ()-->() RETURN p")]
    [InlineData("INSERT (:P)-->(:P)")]
    [InlineData("CREATE (a:P)-->(b:P)")]
    [InlineData("MATCH (a), (b) INSERT (a)-->(b)")]
    [InlineData("MATCH (a)-->(b)")]
    public void Parse_CypherArrowAfterElement_ShouldReportGql0008(string gql)
        => AssertGql0008(gql, Parse(gql), line: 1);

    [Fact(DisplayName = "Cohesion Test [Graph.Language] - Cypher arrows: GQL0008 reports the line the -- starts on")]
    public void Parse_CypherArrowOnALaterLine_ShouldReportThatLine()
        => AssertGql0008("MATCH (a)\r\n-[r]->(b)-->(c)\r\nRETURN a", Parse("MATCH (a)\r\n-[r]->(b)-->(c)\r\nRETURN a"), line: 2);

    /// <param name="gql">A Cypher left arrow, which never lexes as a comment after the element.</param>
    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Cypher arrows: <-- and <--> keep GQL0002")]
    [InlineData("MATCH (a)<--(b) RETURN a")]
    [InlineData("MATCH (a)<-->(b) RETURN a")]
    public void Parse_CypherLeftArrow_ShouldReportGql0002(string gql)
        => Parse(gql).Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("GQL0002");

    /// <summary>
    /// A comment separated from the element by whitespace, or one after a predicate's ')', keeps
    /// its ISO meaning, and the clause on the next line is parsed.
    /// </summary>
    /// <param name="gql">A statement with an ISO comment.</param>
    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Cypher arrows: ISO comments elsewhere keep their meaning")]
    [InlineData("MATCH (a) -- comment\nRETURN a")]
    [InlineData("MATCH (a)-[r]->(b) -- note\nRETURN a")]
    [InlineData("MATCH (a) WHERE (a.age > 1)-- note\nRETURN a")]
    [InlineData("MATCH (a)\n-- note\nRETURN a")]
    [InlineData("MATCH (a) RETURN a-- note")]
    public void Parse_IsoComment_ShouldStayAComment(string gql)
    {
        // Act
        var statement = Parse(gql);

        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        statement.GqlExpression.Projections.ShouldHaveSingleItem().Variable.ShouldBe("a");
    }

    /// <summary>
    /// The documented residual: whitespace, or a block comment, between the element and the
    /// dashes leaves an ISO comment, so <c>(a) --&gt;(b)</c> still reads as <c>(a)</c> plus a
    /// comment. GQL0008 keys on the exact offset, as ISO comments must keep working.
    /// </summary>
    /// <param name="gql">A Cypher arrow separated from its element.</param>
    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Cypher arrows: a separated --> is an ISO comment (documented residual)")]
    [InlineData("MATCH (a) -->(b)\nRETURN a")]
    [InlineData("MATCH (a)/* c */-->(b)\nRETURN a")]
    public void Parse_SeparatedCypherArrow_ShouldStayAComment(string gql)
    {
        // Act
        var statement = Parse(gql);

        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        statement.GqlExpression.Matches.ShouldHaveSingleItem().Relationships.ShouldBeEmpty();
    }

    private static void AssertGql0008(string gql, GqlQueryStatement statement, int line)
    {
        var error = statement.Diagnostics.ShouldHaveSingleItem();
        error.Code.ShouldBe("GQL0008");
        int start = error.Start.ShouldNotBeNull();
        start.ShouldBe(gql.IndexOf("--", StringComparison.Ordinal));
        error.End.ShouldBe(start + 2);
        gql.Substring(start, 2).ShouldBe("--");
        error.Line.ShouldBe(line);
        error.Message!.ShouldContain("'->', '<-' or '-'", Case.Sensitive);
        // Parsing stops at the arrow: nothing after it, on its line or the next, is read.
        statement.GqlExpression.Projections.ShouldBeEmpty();
        statement.GqlExpression.DeleteVariables.ShouldBeEmpty();
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
