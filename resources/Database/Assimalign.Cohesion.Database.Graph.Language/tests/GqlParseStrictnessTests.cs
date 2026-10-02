using System;
using System.Linq;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Graph.Language.Tests;

/// <summary>
/// Parse strictness (#1101): a stray character is one GQL0002 that binds nothing, an ISO
/// construct outside the subset is one COHDBL001 at its own span, and every statement form
/// still rejects leftover text. #1139 flipped the label-expression and <c>IS</c> pins to
/// supported cases (<c>GqlLabelExpressionParserTests</c>) and added the tilde-edge pins;
/// <c>GqlCypherArrowTests</c> owns <c>--</c> after a pattern element.
/// </summary>
public sealed class GqlParseStrictnessTests
{
    /// <param name="marked">The statement; the stray character sits between '«' and '»'.</param>
    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Strictness: a stray character is one GQL0002 at its span and binds nothing")]
    [InlineData("MATCH (a) RETURN a.x «?»")]
    [InlineData("MATCH (a) RETURN a «#»;")]
    [InlineData("MATCH (a) WHERE a.x = «?» RETURN a")]
    [InlineData("MATCH («^») RETURN a")]
    [InlineData("INSERT (a:Person {name: «§»})")]
    [InlineData("MATCH (a) RETURN a «\U0001F643»")]
    [InlineData("INSERT (a:Person {age: «٣»})")]
    [InlineData("«﻿»MATCH (a) RETURN a")]
    public void Parse_StrayCharacter_ShouldReportOneSyntaxErrorAndBindNothing(string marked)
    {
        // Arrange
        var (gql, start, end) = Unmark(marked);

        // Act
        var statement = Parse(gql);

        // Assert
        var error = statement.Diagnostics.ShouldHaveSingleItem();
        error.Code.ShouldBe("GQL0002");
        error.Start.ShouldBe(start);
        error.End.ShouldBe(end);
        error.Message!.ShouldStartWith("Unexpected character ", Case.Sensitive);
        var query = statement.GqlExpression;
        query.Matches.ShouldBeEmpty();
        query.Creates.ShouldBeEmpty();
        query.Projections.ShouldBeEmpty();
        query.Predicate.ShouldBeNull();
    }

    /// <summary>
    /// Each used to fail with GQL0002 at a downstream token, or with two COHDBL001s. Each is now
    /// exactly one COHDBL001 at the construct's span, named as the construct.
    /// </summary>
    /// <param name="marked">The statement; the reported span sits between '«' and '»'.</param>
    /// <param name="construct">The construct the message names.</param>
    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Strictness: ISO constructs report one COHDBL001 at their span and no GQL0002")]
    // IS outside a labeled predicate stays unsupported; the IS that RETURN names is the one
    // construct, as before #1101.
    [InlineData("MATCH (is) RETURN «is»", "IS")]
    // Undirected (tilde) edges (#1139 pins): the engine stores only directed relationships.
    [InlineData("MATCH (a)«~[r]~»(b) RETURN a", "UNDIRECTED EDGE")]
    [InlineData("MATCH (a)«<~[r:T]~»(b) RETURN a", "UNDIRECTED EDGE")]
    [InlineData("MATCH (a)«~[r {k: 1}]~>»(b) RETURN a", "UNDIRECTED EDGE")]
    [InlineData("MATCH (a)«~»(b) RETURN a", "UNDIRECTED EDGE")]
    [InlineData("MATCH (a)«<~»(b) RETURN a", "UNDIRECTED EDGE")]
    [InlineData("MATCH (a)«~>»(b) RETURN a", "UNDIRECTED EDGE")]
    [InlineData("INSERT (a:P)«~>»(b:P)", "UNDIRECTED EDGE")]
    [InlineData("MATCH (a)-[r]->(b)«~»(c) RETURN a", "UNDIRECTED EDGE")]
    // Path mode prefixes (pins)
    [InlineData("MATCH «TRAIL» (a)-[]->(b)", "TRAIL PATH MODE")]
    [InlineData("MATCH p = «ACYCLIC» (a)-[]->(b)", "ACYCLIC PATH MODE")]
    [InlineData("MATCH «WALK» (a)-[]->(b)", "WALK PATH MODE")]
    [InlineData("MATCH p = «SIMPLE» (a)-[]->(b) RETURN p", "SIMPLE PATH MODE")]
    [InlineData("MATCH «TRAIL PATH» (a)-[]->(b) RETURN a", "TRAIL PATH MODE")]
    // Path search prefixes (pins): one prefix, not ALL or ANY plus SHORTEST PATH
    [InlineData("MATCH «ALL SHORTEST» (a)-[]->(b)", "ALL SHORTEST")]
    [InlineData("MATCH «ANY SHORTEST» (a)-[]->(b)", "ANY SHORTEST")]
    [InlineData("MATCH p = «ANY SHORTEST PATHS» (a)-[]->(b) RETURN p", "ANY SHORTEST")]
    // A path search prefix owns its path mode and PATH/PATHS (ISO/IEC 39075 <path search prefix>)
    [InlineData("MATCH «ANY SHORTEST TRAIL» (a)-[]->(b) RETURN a", "ANY SHORTEST")]
    [InlineData("MATCH «ALL TRAIL» (a)-[]->(b) RETURN a", "ALL PATH SEARCH")]
    [InlineData("MATCH «ALL» (a)-[]->(b) RETURN a", "ALL PATH SEARCH")]
    [InlineData("MATCH «ANY» (a)-[]->(b) RETURN a", "ANY PATH SEARCH")]
    [InlineData("MATCH «ANY 2 WALK PATHS» (a)-[]->(b) RETURN a", "ANY PATH SEARCH")]
    [InlineData("MATCH «SHORTEST 2 GROUPS» (a)-[]->(b) RETURN a", "SHORTEST PATH")]
    // A procedure's YIELD belongs to its CALL
    [InlineData("MATCH (a) «CALL» db.labels() YIELD label RETURN label", "CALL")]
    // Delete without detaching (pin)
    [InlineData("MATCH (n) «NODETACH DELETE» n", "NODETACH DELETE")]
    // MERGE (pin; already COHDBL001 before #1101)
    [InlineData("«MERGE» (n)", "MERGE")]
    public void Parse_IsoConstruct_ShouldReportOneCapabilityDiagnostic(string marked, string construct)
    {
        // Arrange
        var (gql, start, end) = Unmark(marked);

        // Act
        var statement = Parse(gql);

        // Assert
        var diagnostic = statement.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("COHDBL001");
        diagnostic.Message.ShouldBe($"The {construct} clause is not supported by the GQL surface of this database model.");
        diagnostic.Start.ShouldBe(start);
        diagnostic.End.ShouldBe(end);
    }

    /// <summary>A path-mode or NODETACH word that is not in prefix position is still a name.</summary>
    /// <param name="gql">A statement that uses those words as names.</param>
    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Strictness: positional words outside their position stay names")]
    [InlineData("MATCH trail = (a)-[r]->(b) RETURN trail")]
    [InlineData("MATCH (walk)-[simple]->(acyclic) RETURN walk, simple, acyclic")]
    [InlineData("MATCH (nodetach) DELETE nodetach")]
    [InlineData("MATCH (a:Trail {walk: 1}) RETURN a.acyclic")]
    public void Parse_PositionalWordAsName_ShouldStillParse(string gql)
        => Parse(gql).Diagnostics.ShouldBeEmpty();

    /// <summary>
    /// Pins the existing leftover-token rejection after every statement form, with and
    /// without a separating ';'. Text after a complete statement is never dropped.
    /// </summary>
    /// <param name="form">A complete statement form.</param>
    /// <param name="separator">Text between the form and the leftover token.</param>
    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Strictness: every statement form rejects trailing tokens (pin)")]
    [InlineData("MATCH (a) RETURN a.name", " ")]
    [InlineData("MATCH (a) RETURN a.name", "; ")]
    [InlineData("INSERT (a:Person {name: 'x'})", " ")]
    [InlineData("INSERT (a:Person {name: 'x'})", ";")]
    [InlineData("CREATE (a:Person)", " ")]
    [InlineData("CREATE (a:Person)", "; ")]
    [InlineData("MATCH (a) DELETE a", " ")]
    [InlineData("MATCH (a) DELETE a", "; ")]
    [InlineData("MATCH (a) DETACH DELETE a", " ")]
    [InlineData("MATCH (a) DETACH DELETE a", "; ")]
    [InlineData("SHOW LABELS", " ")]
    [InlineData("SHOW LABELS", "; ")]
    public void Parse_TrailingToken_ShouldReportOneSyntaxError(string form, string separator)
    {
        // Arrange
        string gql = form + separator + "extra";

        // Act
        var statement = Parse(gql);

        // Assert
        var error = statement.Diagnostics.ShouldHaveSingleItem();
        error.Code.ShouldBe("GQL0002");
        error.Message.ShouldBe("Unexpected token; exactly one statement is accepted.");
        error.Start.ShouldBe(gql.IndexOf("extra", StringComparison.Ordinal));
    }

    private static (string Gql, int Start, int End) Unmark(string marked)
    {
        int start = marked.IndexOf('«', StringComparison.Ordinal);
        int end = marked.IndexOf('»', StringComparison.Ordinal) - 1;
        return (marked.Replace("«", string.Empty, StringComparison.Ordinal).Replace("»", string.Empty, StringComparison.Ordinal), start, end);
    }

    private static GqlQueryStatement Parse(string gql) => (GqlQueryStatement)new GqlQueryParser().Parse(gql);
}
