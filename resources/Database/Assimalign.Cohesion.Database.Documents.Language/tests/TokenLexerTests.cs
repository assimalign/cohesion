using System;
using System.Collections.Generic;
using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Language.Oql;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Language.Oql.Tests;

public class TokenLexerTests
{
    private static List<(TokenType Type, string Value, int Position)> Tokenize(string input)
    {
        var tokens = new List<(TokenType, string, int)>();
        var lexer = new TokenLexer(input, OqlLanguage.CreateLexerOptions());

        foreach (var token in lexer)
        {
            tokens.Add((token.Type, token.Value.ToString(), token.Position));
        }

        return tokens;
    }

    // ── Keywords ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("SELECT")]
    [InlineData("FROM")]
    [InlineData("WHERE")]
    [InlineData("DEFINE")]
    [InlineData("ELEMENT")]
    [InlineData("FLATTEN")]
    [InlineData("STRUCT")]
    [InlineData("LIST")]
    [InlineData("SET")]
    [InlineData("BAG")]
    public void MoveNext_OqlKeyword_ReturnsKeywordToken(string keyword)
    {
        var tokens = Tokenize(keyword);

        tokens.ShouldHaveSingleItem()
            .ShouldBe((TokenType.Keyword, keyword, 0));
    }

    [Theory]
    [InlineData("select")]
    [InlineData("Select")]
    [InlineData("sElEcT")]
    public void MoveNext_KeywordIsCaseInsensitive(string keyword)
    {
        var tokens = Tokenize(keyword);

        tokens.ShouldHaveSingleItem()
            .Type.ShouldBe(TokenType.Keyword);
    }

    // ── Functions ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("COUNT")]
    [InlineData("SUM")]
    [InlineData("AVG")]
    [InlineData("MIN")]
    [InlineData("MAX")]
    [InlineData("ABS")]
    public void MoveNext_OqlFunction_ReturnsFunctionToken(string function)
    {
        var tokens = Tokenize(function);

        tokens.ShouldHaveSingleItem()
            .ShouldBe((TokenType.Function, function, 0));
    }

    [Fact]
    public void MoveNext_FunctionIsCaseInsensitive()
    {
        var tokens = Tokenize("count");

        tokens.ShouldHaveSingleItem()
            .Type.ShouldBe(TokenType.Function);
    }

    // ── Identifiers ────────────────────────────────────────────────────

    [Theory]
    [InlineData("name")]
    [InlineData("_private")]
    [InlineData("col1")]
    [InlineData("Customer")]
    public void MoveNext_Identifier_ReturnsIdentifierToken(string identifier)
    {
        var tokens = Tokenize(identifier);

        tokens.ShouldHaveSingleItem()
            .ShouldBe((TokenType.Identifier, identifier, 0));
    }

    [Fact]
    public void MoveNext_QuotedIdentifier_ReturnsQuotedIdentifierToken()
    {
        var tokens = Tokenize("\"my column\"");

        tokens.ShouldHaveSingleItem()
            .ShouldBe((TokenType.QuotedIdentifier, "\"my column\"", 0));
    }

    // ── String Literals ────────────────────────────────────────────────

    [Fact]
    public void MoveNext_StringLiteral_ReturnsStringToken()
    {
        var tokens = Tokenize("'hello world'");

        tokens.ShouldHaveSingleItem()
            .ShouldBe((TokenType.String, "'hello world'", 0));
    }

    [Fact]
    public void MoveNext_StringWithEscapedQuote_ReturnsSingleStringToken()
    {
        var tokens = Tokenize("'it''s'");

        tokens.ShouldHaveSingleItem()
            .ShouldBe((TokenType.String, "'it''s'", 0));
    }

    [Fact]
    public void MoveNext_EmptyString_ReturnsStringToken()
    {
        var tokens = Tokenize("''");

        tokens.ShouldHaveSingleItem()
            .ShouldBe((TokenType.String, "''", 0));
    }

    // ── Numeric Literals ───────────────────────────────────────────────

    [Fact]
    public void MoveNext_Integer_ReturnsIntegerToken()
    {
        var tokens = Tokenize("42");

        tokens.ShouldHaveSingleItem()
            .ShouldBe((TokenType.Integer, "42", 0));
    }

    [Fact]
    public void MoveNext_Float_ReturnsFloatToken()
    {
        var tokens = Tokenize("3.14");

        tokens.ShouldHaveSingleItem()
            .ShouldBe((TokenType.Float, "3.14", 0));
    }

    [Fact]
    public void MoveNext_LeadingDotFloat_ReturnsFloatToken()
    {
        var tokens = Tokenize(".5");

        tokens.ShouldHaveSingleItem()
            .ShouldBe((TokenType.Float, ".5", 0));
    }

    [Theory]
    [InlineData("1e10", "1e10")]
    [InlineData("2.5E-3", "2.5E-3")]
    [InlineData("7E+2", "7E+2")]
    public void MoveNext_ScientificNotation_ReturnsFloatToken(string input, string expected)
    {
        var tokens = Tokenize(input);

        tokens.ShouldHaveSingleItem()
            .ShouldBe((TokenType.Float, expected, 0));
    }

    [Fact]
    public void MoveNext_IntegerFollowedByDotDot_DoesNotConsumeDotDot()
    {
        var tokens = Tokenize("1..10");

        tokens.Count.ShouldBe(3);
        tokens[0].ShouldBe((TokenType.Integer, "1", 0));
        tokens[1].ShouldBe((TokenType.DotDot, "..", 1));
        tokens[2].ShouldBe((TokenType.Integer, "10", 3));
    }

    // ── Operators ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("+", TokenType.Plus)]
    [InlineData("-", TokenType.Minus)]
    [InlineData("*", TokenType.Asterisk)]
    [InlineData("/", TokenType.Slash)]
    [InlineData("%", TokenType.Percent)]
    [InlineData("=", TokenType.Equals)]
    [InlineData("<", TokenType.LessThan)]
    [InlineData(">", TokenType.GreaterThan)]
    [InlineData("~", TokenType.Tilde)]
    [InlineData("!", TokenType.Bang)]
    [InlineData("&", TokenType.Ampersand)]
    [InlineData("|", TokenType.Pipe)]
    public void MoveNext_SingleCharOperator_ReturnsCorrectToken(string input, TokenType expected)
    {
        var tokens = Tokenize(input);

        tokens.ShouldHaveSingleItem()
            .Type.ShouldBe(expected);
    }

    [Theory]
    [InlineData("<=", TokenType.LessEqual)]
    [InlineData(">=", TokenType.GreaterEqual)]
    [InlineData("!=", TokenType.NotEquals)]
    [InlineData("<>", TokenType.NotEquals)]
    [InlineData("||", TokenType.Concat)]
    [InlineData("::", TokenType.ColonColon)]
    [InlineData("..", TokenType.DotDot)]
    [InlineData("->", TokenType.RightArrow)]
    [InlineData("<-", TokenType.LeftArrow)]
    public void MoveNext_MultiCharOperator_ReturnsCorrectToken(string input, TokenType expected)
    {
        var tokens = Tokenize(input);

        tokens.ShouldHaveSingleItem()
            .Type.ShouldBe(expected);
    }

    // ── Punctuation ────────────────────────────────────────────────────

    [Theory]
    [InlineData("(", TokenType.LeftParen)]
    [InlineData(")", TokenType.RightParen)]
    [InlineData("[", TokenType.LeftBracket)]
    [InlineData("]", TokenType.RightBracket)]
    [InlineData("{", TokenType.LeftBrace)]
    [InlineData("}", TokenType.RightBrace)]
    [InlineData(",", TokenType.Comma)]
    [InlineData(";", TokenType.Semicolon)]
    [InlineData(".", TokenType.Dot)]
    [InlineData(":", TokenType.Colon)]
    public void MoveNext_Punctuation_ReturnsCorrectToken(string input, TokenType expected)
    {
        var tokens = Tokenize(input);

        tokens.ShouldHaveSingleItem()
            .Type.ShouldBe(expected);
    }

    // ── Parameters ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("$1", "$1")]
    [InlineData("$name", "$name")]
    [InlineData("$user_id", "$user_id")]
    public void MoveNext_DollarParameter_ReturnsParameterToken(string input, string expected)
    {
        var tokens = Tokenize(input);

        tokens.ShouldHaveSingleItem()
            .ShouldBe((TokenType.Parameter, expected, 0));
    }

    [Theory]
    [InlineData("@id", "@id")]
    [InlineData("@user_name", "@user_name")]
    public void MoveNext_AtParameter_ReturnsParameterToken(string input, string expected)
    {
        var tokens = Tokenize(input);

        tokens.ShouldHaveSingleItem()
            .ShouldBe((TokenType.Parameter, expected, 0));
    }

    // ── Comments ───────────────────────────────────────────────────────

    [Fact]
    public void MoveNext_LineComment_ReturnsCommentToken()
    {
        var tokens = Tokenize("-- this is a comment");

        tokens.ShouldHaveSingleItem()
            .ShouldBe((TokenType.Comment, "-- this is a comment", 0));
    }

    [Fact]
    public void MoveNext_BlockComment_ReturnsCommentToken()
    {
        var tokens = Tokenize("/* block */");

        tokens.ShouldHaveSingleItem()
            .ShouldBe((TokenType.Comment, "/* block */", 0));
    }

    [Fact]
    public void MoveNext_NestedBlockComment_ReturnsCommentToken()
    {
        var tokens = Tokenize("/* outer /* inner */ still comment */");

        tokens.ShouldHaveSingleItem()
            .Type.ShouldBe(TokenType.Comment);
    }

    [Fact]
    public void MoveNext_CommentBeforeCode_BothTokensProduced()
    {
        var tokens = Tokenize("-- comment\nSELECT");

        tokens.Count.ShouldBe(2);
        tokens[0].Type.ShouldBe(TokenType.Comment);
        tokens[1].ShouldBe((TokenType.Keyword, "SELECT", 11));
    }

    // ── Edge Cases ─────────────────────────────────────────────────────

    [Fact]
    public void MoveNext_EmptyInput_ReturnsFalse()
    {
        var lexer = new TokenLexer("", OqlLanguage.CreateLexerOptions());

        lexer.MoveNext().ShouldBeFalse();
        lexer.Current.Type.ShouldBe(TokenType.Eof);
    }

    [Fact]
    public void MoveNext_WhitespaceOnly_ReturnsFalse()
    {
        var lexer = new TokenLexer("   \t\n  ", OqlLanguage.CreateLexerOptions());

        lexer.MoveNext().ShouldBeFalse();
    }

    [Fact]
    public void MoveNext_TrackPositions_PositionsAreCorrect()
    {
        // "a + b" → a(0) +(2) b(4)
        var tokens = Tokenize("a + b");

        tokens.Count.ShouldBe(3);
        tokens[0].Position.ShouldBe(0);
        tokens[1].Position.ShouldBe(2);
        tokens[2].Position.ShouldBe(4);
    }

    [Fact]
    public void Reset_AfterPartialScan_RestartsFromBeginning()
    {
        var lexer = new TokenLexer("SELECT name", OqlLanguage.CreateLexerOptions());

        lexer.MoveNext(); // SELECT
        lexer.Reset();
        lexer.MoveNext(); // SELECT again

        lexer.Current.Type.ShouldBe(TokenType.Keyword);
        lexer.Current.Value.ToString().ShouldBe("SELECT");
    }

    // ── Object Navigation (OQL-specific patterns) ──────────────────────

    [Fact]
    public void MoveNext_DotNavigation_ProducesDotTokens()
    {
        // OQL: person.address.city
        var tokens = Tokenize("person.address.city");

        tokens.Count.ShouldBe(5);
        tokens[0].ShouldBe((TokenType.Identifier, "person", 0));
        tokens[1].ShouldBe((TokenType.Dot, ".", 6));
        tokens[2].ShouldBe((TokenType.Identifier, "address", 7));
        tokens[3].ShouldBe((TokenType.Dot, ".", 14));
        tokens[4].ShouldBe((TokenType.Identifier, "city", 15));
    }

    // ── Full OQL Queries ───────────────────────────────────────────────

    [Fact]
    public void MoveNext_SimpleSelectQuery_TokenizesCorrectly()
    {
        var tokens = Tokenize("SELECT name FROM customers WHERE age > 21");

        tokens.Count.ShouldBe(8);
        tokens[0].ShouldBe((TokenType.Keyword, "SELECT", 0));
        tokens[1].ShouldBe((TokenType.Identifier, "name", 7));
        tokens[2].ShouldBe((TokenType.Keyword, "FROM", 12));
        tokens[3].ShouldBe((TokenType.Identifier, "customers", 17));
        tokens[4].ShouldBe((TokenType.Keyword, "WHERE", 27));
        tokens[5].ShouldBe((TokenType.Identifier, "age", 33));
        tokens[6].ShouldBe((TokenType.GreaterThan, ">", 37));
        tokens[7].ShouldBe((TokenType.Integer, "21", 39));
    }

    [Fact]
    public void MoveNext_SelectWithFunction_TokenizesCorrectly()
    {
        var tokens = Tokenize("SELECT COUNT(*) FROM orders");

        tokens.Count.ShouldBe(7);
        tokens[0].ShouldBe((TokenType.Keyword, "SELECT", 0));
        tokens[1].ShouldBe((TokenType.Function, "COUNT", 7));
        tokens[2].ShouldBe((TokenType.LeftParen, "(", 12));
        tokens[3].ShouldBe((TokenType.Asterisk, "*", 13));
        tokens[4].ShouldBe((TokenType.RightParen, ")", 14));
        tokens[5].ShouldBe((TokenType.Keyword, "FROM", 16));
        tokens[6].ShouldBe((TokenType.Identifier, "orders", 21));
    }

    [Fact]
    public void MoveNext_CollectionConstructor_TokenizesCorrectly()
    {
        // OQL: STRUCT(name: 'John', age: 30)
        var tokens = Tokenize("STRUCT(name: 'John', age: 30)");

        tokens.Count.ShouldBe(10);
        tokens[0].Type.ShouldBe(TokenType.Keyword);   // STRUCT
        tokens[1].Type.ShouldBe(TokenType.LeftParen);  // (
        tokens[2].Type.ShouldBe(TokenType.Identifier); // name
        tokens[3].Type.ShouldBe(TokenType.Colon);      // :
        tokens[4].Type.ShouldBe(TokenType.String);     // 'John'
        tokens[5].Type.ShouldBe(TokenType.Comma);      // ,
        tokens[6].Type.ShouldBe(TokenType.Identifier); // age
        tokens[7].Type.ShouldBe(TokenType.Colon);      // :
        tokens[8].Type.ShouldBe(TokenType.Integer);    // 30
        tokens[9].Type.ShouldBe(TokenType.RightParen); // )
    }

    [Fact]
    public void MoveNext_FlattenQuery_TokenizesCorrectly()
    {
        // OQL: SELECT FLATTEN(SELECT addresses FROM customers)
        var tokens = Tokenize("SELECT FLATTEN(SELECT addresses FROM customers)");

        tokens.Count.ShouldBe(8);
        tokens[0].Type.ShouldBe(TokenType.Keyword);    // SELECT
        tokens[1].Type.ShouldBe(TokenType.Keyword);    // FLATTEN (keyword takes precedence)
        tokens[2].Type.ShouldBe(TokenType.LeftParen);
        tokens[3].Type.ShouldBe(TokenType.Keyword);    // SELECT
        tokens[4].Type.ShouldBe(TokenType.Identifier); // addresses
        tokens[5].Type.ShouldBe(TokenType.Keyword);    // FROM
        tokens[6].Type.ShouldBe(TokenType.Identifier); // customers
        tokens[7].Type.ShouldBe(TokenType.RightParen); // )
    }

    [Fact]
    public void MoveNext_QueryWithStringAndComparison_TokenizesCorrectly()
    {
        var tokens = Tokenize("SELECT name FROM people WHERE name LIKE 'A%' AND age >= 18");

        tokens.Count.ShouldBe(12);
        tokens[0].Type.ShouldBe(TokenType.Keyword);    // SELECT
        tokens[1].Type.ShouldBe(TokenType.Identifier); // name
        tokens[2].Type.ShouldBe(TokenType.Keyword);    // FROM
        tokens[3].Type.ShouldBe(TokenType.Identifier); // people
        tokens[4].Type.ShouldBe(TokenType.Keyword);    // WHERE
        tokens[5].Type.ShouldBe(TokenType.Identifier); // name
        tokens[6].Type.ShouldBe(TokenType.Keyword);    // LIKE
        tokens[7].Type.ShouldBe(TokenType.String);     // 'A%'
        tokens[8].Type.ShouldBe(TokenType.Keyword);    // AND
        tokens[9].Type.ShouldBe(TokenType.Identifier); // age
        tokens[10].Type.ShouldBe(TokenType.GreaterEqual); // >=
        tokens[11].Type.ShouldBe(TokenType.Integer);   // 18
    }

    [Fact]
    public void MoveNext_QueryWithParameter_TokenizesCorrectly()
    {
        var tokens = Tokenize("SELECT name FROM users WHERE id = $1");

        tokens.Count.ShouldBe(8);
        tokens[7].ShouldBe((TokenType.Parameter, "$1", 34));
    }

    [Fact]
    public void MoveNext_ElementQuery_TokenizesCorrectly()
    {
        // OQL: ELEMENT(SELECT salary FROM employees WHERE name = 'Alice')
        var tokens = Tokenize("ELEMENT(SELECT salary FROM employees WHERE name = 'Alice')");

        tokens[0].Type.ShouldBe(TokenType.Keyword); // ELEMENT (keyword takes precedence)
        tokens[1].Type.ShouldBe(TokenType.LeftParen);
        tokens[2].Type.ShouldBe(TokenType.Keyword); // SELECT
        tokens[tokens.Count - 1].Type.ShouldBe(TokenType.RightParen);
    }

    // ── Foreach / Enumerator Pattern ───────────────────────────────────

    [Fact]
    public void GetEnumerator_MultipleForeachLoops_EachStartsFromBeginning()
    {
        var lexer = new TokenLexer("SELECT name", OqlLanguage.CreateLexerOptions());

        var first = new List<string>();
        foreach (var token in lexer)
        {
            first.Add(token.Value.ToString());
        }

        var second = new List<string>();
        foreach (var token in lexer)
        {
            second.Add(token.Value.ToString());
        }

        first.ShouldBe(second);
    }
}
