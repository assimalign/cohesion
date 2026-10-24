using System;
using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language.Tests;

/// <summary>
/// Parse strictness (#1101): a stray character is one CD00002 that binds nothing, a
/// recognized-unsupported word is one CK00001 at its own span wherever a clause can start,
/// and every statement form still rejects leftover text.
/// </summary>
public sealed class OqlParseStrictnessTests
{
    /// <param name="marked">The statement; the stray character sits between '«' and '»'.</param>
    [Theory(DisplayName = "Cohesion Test [Documents.Language] - Strictness: a stray character is one CD00002 at its span and binds nothing")]
    [InlineData("SELECT * FROM people «^»")]
    [InlineData("SELECT * FROM people «#»;")]
    [InlineData("SELECT * FROM c WHERE c.a = «?»", true)]
    [InlineData("SELECT «?» FROM people", true)]
    [InlineData("SELECT a «§» b FROM people")]
    [InlineData("CREATE INDEX ix ON people («?»)")]
    [InlineData("SELECT * FROM people «\U0001F643»")]
    [InlineData("SELECT * FROM c WHERE c.a = «٣»")]
    [InlineData("«﻿»SELECT * FROM c")]
    public void Parse_StrayCharacter_ShouldReportOneSyntaxErrorAndBindNothing(string marked, bool parameterForm = false)
    {
        // Arrange
        var (oql, start, end) = Unmark(marked);

        // Act
        var statement = Parse(oql);

        // Assert
        var error = statement.Diagnostics.ShouldHaveSingleItem();
        error.Code.ShouldBe("CD00002");
        error.Start.ShouldBe(start);
        error.End.ShouldBe(end);
        error.Message!.ShouldStartWith(parameterForm ? "Unsupported parameter form;" : "Unexpected character ", Case.Sensitive);
        var select = statement.OqlExpression.ShouldBeOfType<OqlSelectExpression>();
        select.Collection.ShouldBeEmpty();
        select.Alias.ShouldBeNull();
        select.Projections.ShouldBeEmpty();
        select.Predicate.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Documents.Language] - Strictness: a parameter-shaped ? is never a path or a NULL literal")]
    public void Parse_QuestionMarkInExpression_ShouldProduceNoPathOrNullLiteral()
    {
        // Act
        var statement = Parse("SELECT * FROM c WHERE c.a = ?");

        // Assert
        statement.Diagnostics.ShouldHaveSingleItem().Message!.ShouldContain("use $name, @name, or $1");
        statement.OqlExpression.ShouldBeOfType<OqlSelectExpression>().Predicate.ShouldBeNull();
    }

    /// <summary>
    /// Each used to fall through to CD00002, or to bind a word as an alias and then fail at
    /// the next token. Each is now one CK00001 at the word that names the construct.
    /// </summary>
    /// <param name="marked">The statement; the reported span sits between '«' and '»'.</param>
    /// <param name="construct">The construct the message names.</param>
    [Theory(DisplayName = "Cohesion Test [Documents.Language] - Strictness: SQL++ words report one CK00001 and no CD00002")]
    [InlineData("«MERGE» INTO c USING d ON c.id = d.id WHEN MATCHED THEN UPDATE SET c.a = d.a", "MERGE")]
    [InlineData("SELECT c.fetch «first» FROM c", "FIRST")]
    [InlineData("SELECT c.limit «ALL» FROM c", "ALL")]
    [InlineData("SELECT c.offset «ALL» FROM c", "ALL")]
    [InlineData("SELECT fetch «FIRST» FROM c", "FIRST")]
    [InlineData("SELECT c AS fetch «FIRST» FROM c", "FIRST")]
    [InlineData("SELECT c AS limit «ALL» FROM c", "ALL")]
    // A Clause word directly after the collection is a construct, never an AS-less alias.
    // A set operation and its ALL or DISTINCT are one construct.
    [InlineData("SELECT * FROM c «UNION ALL» SELECT * FROM d", "UNION")]
    [InlineData("SELECT * FROM c «EXCEPT DISTINCT» SELECT * FROM d", "EXCEPT")]
    // ODMG's FOR ALL owns its SATISFIES, which is not reported again.
    [InlineData("SELECT * FROM c WHERE «FOR» ALL x IN c.items SATISFIES x > 1", "FOR ALL")]
    public void Parse_UnsupportedWord_ShouldReportOneCapabilityDiagnostic(string marked, string construct)
    {
        // Arrange
        var (oql, start, end) = Unmark(marked);

        // Act
        var statement = Parse(oql);

        // Assert
        var diagnostic = statement.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("CK00001");
        diagnostic.Message.ShouldBe($"The {construct} clause is not supported by the OQL surface of this database model.");
        diagnostic.Start.ShouldBe(start);
        diagnostic.End.ShouldBe(end);
    }

    /// <summary>
    /// Recognized-but-unsupported words stay positional, not lexer keywords: where a clause
    /// cannot start they are ordinary names.
    /// </summary>
    /// <param name="oql">A statement that uses those words as names.</param>
    [Theory(DisplayName = "Cohesion Test [Documents.Language] - Strictness: words outside a clause position stay field names")]
    [InlineData("SELECT p.flatten, p.select, p.array FROM people p")]
    [InlineData("SELECT merge FROM c")]
    [InlineData("SELECT a AS limit FROM c")]
    [InlineData("SELECT upsert, unnest, every, satisfies FROM c")]
    [InlineData("SELECT * FROM c WHERE fetch = 1 AND unnest = 2")]
    [InlineData("SELECT * FROM c ORDER BY fetch DESC")]
    [InlineData("SELECT c.limit FROM c WHERE c.every.satisfies = 1")]
    // A statement verb or quantifier word is a construct only in its own position, so an
    // AS-less FROM alias of that name still parses, as it did before #1101.
    [InlineData("SELECT merge.a FROM c merge")]
    [InlineData("SELECT upsert.a FROM c upsert")]
    [InlineData("SELECT every.a FROM c every")]
    [InlineData("SELECT satisfies.a FROM c satisfies")]
    // AS keeps a Clause word a name.
    [InlineData("SELECT unnest.a FROM c AS unnest")]
    [InlineData("SELECT merge.a FROM c AS merge WHERE merge.b = 1")]
    public void Parse_WordAsName_ShouldStillParse(string oql)
    {
        // Act
        var statement = Parse(oql);

        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        statement.OqlExpression.ShouldBeOfType<OqlSelectExpression>().Projections.ShouldNotBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Documents.Language] - Strictness: FETCH in a field position retains its document path")]
    public void Parse_FetchField_ShouldRetainPath()
    {
        // Act
        var statement = Parse("SELECT fetch FROM c");

        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        var projection = statement.OqlExpression.ShouldBeOfType<OqlSelectExpression>().Projections.ShouldHaveSingleItem();
        projection.Expression.ShouldBeOfType<OqlPathExpression>().Segments.ShouldHaveSingleItem().Name.ShouldBe("fetch");
    }

    [Fact(DisplayName = "Cohesion Test [Documents.Language] - Strictness: UPSERT is an executable document write")]
    public void Parse_Upsert_ShouldProduceSupportedStatement()
    {
        // Arrange / Act
        var statement = Parse("UPSERT INTO c (KEY, VALUE) VALUES ('k', {'a': 1})");
        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        statement.OqlExpression.ShouldBeOfType<OqlUpsertExpression>().Collection.ShouldBe("c");
    }

    [Fact(DisplayName = "Cohesion Test [Documents.Language] - Strictness: OQL declares no ~, so it stays a syntax error (pin)")]
    public void Parse_Tilde_ShouldKeepSyntaxError()
    {
        // Act
        var diagnostics = Parse("SELECT ~a FROM c").Diagnostics.ToArray();

        // Assert: recovery reached the ~ from three rules and used to report it three times.
        var diagnostic = diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("CD00002");
        diagnostic.Start.ShouldBe("SELECT ".Length);
        diagnostic.End.ShouldBe("SELECT ~".Length);
    }

    /// <summary>
    /// Pins the existing leftover-token rejection after every statement form, with and
    /// without a separating ';'. Text after a complete statement is never dropped.
    /// </summary>
    /// <param name="form">A complete statement form.</param>
    /// <param name="separator">Text between the form and the leftover token.</param>
    [Theory(DisplayName = "Cohesion Test [Documents.Language] - Strictness: every statement form rejects trailing tokens (pin)")]
    [InlineData("SELECT * FROM people WHERE age = 1", " ")]
    [InlineData("SELECT * FROM people WHERE age = 1", "; ")]
    [InlineData("SELECT name FROM people ORDER BY name", " ")]
    [InlineData("SELECT name FROM people ORDER BY name", ";")]
    [InlineData("CREATE INDEX ix ON people (address.city)", " ")]
    [InlineData("CREATE INDEX ix ON people (address.city)", "; ")]
    [InlineData("DROP INDEX ix ON people", " 1 ")]
    [InlineData("DROP INDEX ix ON people", "; ")]
    public void Parse_TrailingToken_ShouldReportOneSyntaxError(string form, string separator)
    {
        // Arrange
        string oql = form + separator + "extra";

        // Act
        var statement = Parse(oql);

        // Assert: the error is at the first token after the form, or after its ';'.
        bool terminated = separator.Contains(';', StringComparison.Ordinal);
        var error = statement.Diagnostics.ShouldHaveSingleItem();
        error.Code.ShouldBe("CD00002");
        error.Start.ShouldBe(terminated
            ? oql.IndexOf("extra", StringComparison.Ordinal)
            : form.Length + separator.Length - separator.TrimStart().Length);
        error.Message.ShouldBe(terminated
            ? "Only one statement is accepted per query."
            : $"Unexpected token '{oql[error.Start!.Value..error.End!.Value]}'.");
    }

    private static (string Oql, int Start, int End) Unmark(string marked)
    {
        int start = marked.IndexOf('«', StringComparison.Ordinal);
        int end = marked.IndexOf('»', StringComparison.Ordinal) - 1;
        return (marked.Replace("«", string.Empty, StringComparison.Ordinal).Replace("»", string.Empty, StringComparison.Ordinal), start, end);
    }

    private static OqlQueryStatement Parse(string oql) => new OqlQueryParser().Parse(oql).ShouldBeOfType<OqlQueryStatement>();
}
