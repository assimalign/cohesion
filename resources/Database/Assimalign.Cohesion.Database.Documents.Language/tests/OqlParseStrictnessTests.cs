using System;
using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language.Tests;

/// <summary>
/// Parse strictness (#1101): a stray character is one OQL0002 that binds nothing, a
/// recognized-unsupported word is one COHDBL001 at its own span wherever a clause can start,
/// and every statement form still rejects leftover text.
/// </summary>
public sealed class OqlParseStrictnessTests
{
    /// <param name="marked">The statement; the stray character sits between '«' and '»'.</param>
    [Theory(DisplayName = "Cohesion Test [Documents.Language] - Strictness: a stray character is one OQL0002 at its span and binds nothing")]
    [InlineData("SELECT * FROM people «^»")]
    [InlineData("SELECT * FROM people «#»;")]
    [InlineData("SELECT * FROM c WHERE c.a = «?»")]
    [InlineData("SELECT «?» FROM people")]
    [InlineData("SELECT a «§» b FROM people")]
    [InlineData("CREATE INDEX ix ON people («?»)")]
    [InlineData("SELECT * FROM people «\U0001F643»")]
    public void Parse_StrayCharacter_ShouldReportOneSyntaxErrorAndBindNothing(string marked)
    {
        // Arrange
        var (oql, start, end) = Unmark(marked);

        // Act
        var statement = Parse(oql);

        // Assert
        var error = statement.Diagnostics.ShouldHaveSingleItem();
        error.Code.ShouldBe("OQL0002");
        error.Start.ShouldBe(start);
        error.End.ShouldBe(end);
        error.Message!.ShouldStartWith("Unexpected character ", Case.Sensitive);
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
        statement.Diagnostics.ShouldHaveSingleItem().Message.ShouldBe("Unexpected character '?'; it is not part of OQL.");
        statement.OqlExpression.ShouldBeOfType<OqlSelectExpression>().Predicate.ShouldBeNull();
    }

    /// <summary>
    /// Each used to fall through to OQL0002, or to bind a word as an alias and then fail at
    /// the next token. Each is now one COHDBL001 at the word that names the construct.
    /// </summary>
    /// <param name="marked">The statement; the reported span sits between '«' and '»'.</param>
    /// <param name="construct">The construct the message names.</param>
    [Theory(DisplayName = "Cohesion Test [Documents.Language] - Strictness: SQL++ words report one COHDBL001 and no OQL0002")]
    [InlineData("SELECT * FROM c WHERE a = 1 «LIMIT» 5", "LIMIT")]
    [InlineData("SELECT * FROM c WHERE a = 'x' «LIMIT» 1", "LIMIT")]
    [InlineData("SELECT * FROM c WHERE a IS NULL «LIMIT» 1", "LIMIT")]
    [InlineData("SELECT * FROM c WHERE a = TRUE «LIMIT» 1", "LIMIT")]
    [InlineData("SELECT * FROM c WHERE a = @p «LIMIT» 1", "LIMIT")]
    [InlineData("SELECT * FROM c WHERE c.items[0] = 1 «LIMIT» 1", "LIMIT")]
    [InlineData("SELECT * FROM c ORDER BY c.select «LIMIT» 1", "LIMIT")]
    [InlineData("SELECT * FROM c ORDER BY a DESC «OFFSET» 2", "OFFSET")]
    [InlineData("SELECT * FROM c ORDER BY a ASC «LIMIT» 2", "LIMIT")]
    [InlineData("SELECT * FROM c «LIMIT» 10", "LIMIT")]
    [InlineData("«UPSERT» INTO c (KEY, VALUE) VALUES ('k', {'a': 1})", "UPSERT")]
    [InlineData("«MERGE» INTO c USING d ON c.id = d.id WHEN MATCHED THEN UPDATE SET c.a = d.a", "MERGE")]
    [InlineData("SELECT * FROM c «UNNEST» c.items AS i", "UNNEST")]
    [InlineData("SELECT * FROM c AS x «UNNEST» x.items AS i", "UNNEST")]
    [InlineData("SELECT * FROM c WHERE «EVERY» x IN c.items SATISFIES x > 1 END", "EVERY ... SATISFIES")]
    [InlineData("SELECT * FROM c WHERE «ANY» x IN c.items SATISFIES x > 1 END", "ANY ... SATISFIES")]
    [InlineData("SELECT * FROM c WHERE a = 1 AND «EVERY» x IN c.items SATISFIES x IN [1, 2] END", "EVERY ... SATISFIES")]
    [InlineData("SELECT * FROM c WHERE «EVERY» x IN c.items SATISFIES ANY y IN x.tags SATISFIES y = 1 END END", "EVERY ... SATISFIES")]
    public void Parse_UnsupportedWord_ShouldReportOneCapabilityDiagnostic(string marked, string construct)
    {
        // Arrange
        var (oql, start, end) = Unmark(marked);

        // Act
        var statement = Parse(oql);

        // Assert
        var diagnostic = statement.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("COHDBL001");
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
    [InlineData("SELECT limit, merge FROM c")]
    [InlineData("SELECT a AS limit FROM c")]
    [InlineData("SELECT offset, upsert, unnest, every, satisfies FROM c")]
    [InlineData("SELECT * FROM c WHERE limit = 1 AND merge = 2")]
    [InlineData("SELECT * FROM c ORDER BY limit DESC")]
    [InlineData("SELECT c.limit FROM c WHERE c.every.satisfies = 1")]
    public void Parse_WordAsName_ShouldStillParse(string oql)
    {
        // Act
        var statement = Parse(oql);

        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        statement.OqlExpression.ShouldBeOfType<OqlSelectExpression>().Projections.ShouldNotBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Documents.Language] - Strictness: OQL declares no ~, so it stays a syntax error (pin)")]
    public void Parse_Tilde_ShouldKeepSyntaxError()
    {
        // Act
        var diagnostics = Parse("SELECT ~a FROM c").Diagnostics.ToArray();

        // Assert
        diagnostics.ShouldNotBeEmpty();
        diagnostics.ShouldAllBe(diagnostic => diagnostic.Code == "OQL0002");
        diagnostics[0].Start.ShouldBe("SELECT ".Length);
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
        error.Code.ShouldBe("OQL0002");
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
