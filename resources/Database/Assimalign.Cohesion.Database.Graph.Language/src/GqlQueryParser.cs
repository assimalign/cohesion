using System;
using System.Collections.Generic;
using System.Globalization;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>Parses the executable GQL subset using the shared lexer and analyzer pipeline.</summary>
/// <remarks>Concurrent calls are serialized. Malformed input returns statement diagnostics.</remarks>
public sealed partial class GqlQueryParser : QueryParser
{
    private readonly object _gate = new();
    private readonly List<Lexeme> _tokens = [];
    private readonly List<Diagnostic> _diagnostics = [];
    private string _source = string.Empty;
    private int _position;
    private int _depth;
    private int _comparisons;

    /// <summary>Initializes a GQL parser.</summary>
    /// <param name="options">Optional shared analyzer configuration.</param>
    public GqlQueryParser(QueryParserOptions? options = null) : base(options ?? new QueryParserOptions()) { }

    /// <inheritdoc />
    protected override QueryLanguageProfile Profile => GqlLanguageProfile.Instance;

    /// <inheritdoc />
    public override QueryStatement Parse(ReadOnlySpan<char> query)
    {
        lock (_gate)
        {
            _source = query.ToString();
            return base.Parse(query);
        }
    }

    /// <inheritdoc />
    protected override QueryStatement ParseCore(TokenLexer lexer)
    {
        _tokens.Clear();
        _diagnostics.Clear();
        _position = 0;
        _depth = 0;
        _comparisons = 0;
        int line = 1;
        int scanned = 0;
        while (lexer.MoveNext())
        {
            var token = lexer.Current;
            while (scanned < token.Position)
            {
                if (_source[scanned++] == '\n') { line++; }
            }
            var lexeme = new Lexeme(token.Type, token.Value.ToString(), token.Position,
                token.Position + token.Value.Length, line);
            if (token.Type == TokenType.Comment)
            {
                if (!IsCompleteComment(lexeme.Text)) { Error("GQL0003", "Unterminated block comment.", lexeme); }
            }
            else
            {
                _tokens.Add(lexeme);
                if (token.Type is TokenType.String or TokenType.QuotedIdentifier && !IsCompleteQuoted(lexeme.Text))
                {
                    Error("GQL0003", "Unterminated quoted text.", lexeme);
                }
                else if (token.Type == TokenType.Unrecognized)
                {
                    // A character GQL does not use is never a name or an expression, so the
                    // statement is not parsed and nothing is bound (#1101).
                    Error("GQL0002", $"Unexpected character {DescribeCharacter(lexeme.Text)}; it is not part of GQL.", lexeme);
                }
            }
        }
        while (scanned < _source.Length)
        {
            if (_source[scanned++] == '\n') { line++; }
        }
        _tokens.Add(new Lexeme(TokenType.Eof, string.Empty, _source.Length, _source.Length, line));

        // SHOW has a deliberately separate grammar: catalog definitions are not graph elements.
        // Check attempted composition before generic capability errors (SET, DROP, etc.).
        bool catalogStatement = Is("SHOW");
        if (catalogStatement) { FindCatalogMutation(); }
        else { FindUnsupported(); }
        GqlQueryExpression expression;
        if (Failed) { expression = EmptyExpression(); }
        else if (Current.Type == TokenType.Eof)
        {
            Error("GQL0001", "Query text is empty.", Current);
            expression = EmptyExpression();
        }
        else if (catalogStatement) { expression = ParseCatalog(); }
        else if (Is("MATCH") || Is("CREATE") || Is("INSERT")) { expression = ParseQuery(); }
        else
        {
            if (Is("DELETE") || Is("DETACH") || Is("WHERE") || Is("RETURN"))
            {
                Error("GQL0002", "Expected MATCH, INSERT, or CREATE before this clause.", Current);
            }
            else { Unsupported(Current.Text, Current); }
            expression = EmptyExpression();
        }

        if (!Failed)
        {
            Take(TokenType.Semicolon);
            if (Current.Type != TokenType.Eof)
            {
                Error("GQL0002", "Unexpected token; exactly one statement is accepted.", Current);
            }
        }
        expression.SetStatementText(_source);
        var statement = new GqlQueryStatement(expression);
        foreach (var diagnostic in _diagnostics) { statement.AddDiagnostic(diagnostic); }
        return statement;
    }

    /// <summary>
    /// Finds every recognized construct outside the executable profile, using the
    /// recognized-unsupported table (<see cref="GqlUnsupportedVocabulary"/>). Each construct is
    /// reported once at its own span: a statement whose first word is unsupported reports that
    /// word only, a label expression reports its first operator, and multi-word prefixes such
    /// as <c>ALL SHORTEST</c> and <c>NODETACH DELETE</c> are one construct.
    /// </summary>
    private void FindUnsupported()
    {
        int patternDepth = 0;
        int propertyDepth = 0;
        bool inPredicateOrReturn = false;
        bool inLabelExpression = false;
        bool labelReported = false;
        bool sawCall = false;
        for (int i = 0; i < _tokens.Count; i++)
        {
            var token = _tokens[i];
            if (token.Type == TokenType.Semicolon) { break; }
            if (token.Type == TokenType.LeftBrace && i > 0 && _tokens[i - 1].Type is
                TokenType.RightArrow or TokenType.LeftArrow or TokenType.Minus or TokenType.RightParen)
            {
                Unsupported("QUANTIFIED PATTERN", token);
            }
            if (token.Type is TokenType.LeftParen or TokenType.LeftBracket or TokenType.LeftBrace) { patternDepth++; }
            if (token.Type is TokenType.RightParen or TokenType.RightBracket or TokenType.RightBrace) { patternDepth--; }
            if (token.Type == TokenType.LeftBrace) { propertyDepth++; }
            if (token.Type == TokenType.RightBrace) { propertyDepth--; }

            // A label expression runs from the ':' of a node or edge pattern to its property map
            // or closing bracket. Its first operator names it; the rest belong to it.
            bool inElementPattern = patternDepth > 0 && propertyDepth == 0 && !inPredicateOrReturn;
            if (token.Type is TokenType.LeftBrace or TokenType.RightParen or TokenType.RightBracket)
            {
                inLabelExpression = false;
            }
            if (token.Type == TokenType.Colon && inElementPattern)
            {
                inLabelExpression = true;
                labelReported = false;
            }
            if (token.Type is TokenType.Pipe or TokenType.Ampersand or TokenType.Bang or TokenType.Percent &&
                inLabelExpression)
            {
                if (!labelReported && GqlUnsupportedVocabulary.TryFind(token.Text, out var operation))
                {
                    UnsupportedConstruct(operation.Construct, token, token);
                }
                labelReported = true;
                continue;
            }

            if (token.Type is TokenType.Asterisk or TokenType.DotDot)
            {
                Unsupported("QUANTIFIED PATTERN OR STAR PROJECTION", token);
                continue;
            }
            if (token.Type == TokenType.Parameter)
            {
                Unsupported("PARAMETER", token);
                continue;
            }
            if (token.Type is not (TokenType.Keyword or TokenType.Identifier or TokenType.Function)) { continue; }
            if (i > 0 && _tokens[i - 1].Type is TokenType.Dot or TokenType.Colon) { continue; }
            if (i + 1 < _tokens.Count && _tokens[i + 1].Type == TokenType.Colon) { continue; }
            string value = token.Text.ToUpperInvariant();
            if (patternDepth == 0 && value is "WHERE" or "RETURN") { inPredicateOrReturn = true; }
            if (patternDepth == 0 && value is "MATCH" or "CREATE" or "INSERT" or "DELETE") { inPredicateOrReturn = false; }
            if (inPredicateOrReturn && i + 1 < _tokens.Count && _tokens[i + 1].Type == TokenType.LeftParen &&
                value is not ("WHERE" or "AND" or "RETURN"))
            {
                Unsupported($"FUNCTION {token.Text}", token);
                continue;
            }

            // (n IS A) is ISO's <is label expression>, not a name.
            if (value == "IS" && inElementPattern)
            {
                UnsupportedConstruct(GqlUnsupportedVocabulary.IsLabelExpression, token, token);
                inLabelExpression = true;
                labelReported = true;
                continue;
            }
            if (patternDepth != 0 && !inPredicateOrReturn) { continue; }
            if (!GqlUnsupportedVocabulary.TryFind(value, out var word)) { continue; }

            bool first = i == 0;
            var end = token;
            string construct = word.Construct;
            switch (word.Position)
            {
                case GqlWordPosition.PathMode:
                    // TRAIL (a)... is a path mode; trail = (a)... names a path.
                    if (patternDepth != 0 || inPredicateOrReturn || !StartsPathPattern(i + 1, ref end)) { continue; }
                    break;
                case GqlWordPosition.BeforeDelete:
                    if (!IsWordAt(i + 1, "DELETE")) { continue; }
                    end = _tokens[i + 1];
                    break;
                case GqlWordPosition.LabelExpression:
                    continue;
                default:
                    if (value is "ALL" or "ANY" && !inPredicateOrReturn && IsWordAt(i + 1, "SHORTEST"))
                    {
                        // One path search prefix, not ALL (or ANY) followed by SHORTEST PATH.
                        construct = value + " SHORTEST";
                        end = _tokens[++i];
                        if (IsWordAt(i + 1, "PATH") || IsWordAt(i + 1, "PATHS")) { end = _tokens[++i]; }
                    }
                    else if (value is "STARTS" or "ENDS" && IsWordAt(i + 1, "WITH"))
                    {
                        end = _tokens[++i];
                    }
                    else if (value == "YIELD" && sawCall)
                    {
                        continue; // CALL owns its YIELD.
                    }
                    sawCall |= value == "CALL";
                    break;
            }

            // A statement that does not start with a supported clause is one unsupported
            // construct; the rest of its text belongs to it (SESSION SET GRAPH g, DROP GRAPH g).
            if (UnsupportedConstruct(construct, token, end) && first) { return; }
        }
    }

    /// <summary>
    /// Whether a path pattern starts at <paramref name="index"/>: a <c>(</c>, optionally after
    /// <c>PATH</c> or <c>PATHS</c>, which then extends <paramref name="end"/>.
    /// </summary>
    private bool StartsPathPattern(int index, ref Lexeme end)
    {
        if (IsWordAt(index, "PATH") || IsWordAt(index, "PATHS"))
        {
            end = _tokens[index];
            index++;
        }
        return index < _tokens.Count && _tokens[index].Type == TokenType.LeftParen;
    }

    private bool IsWordAt(int index, string word) => index < _tokens.Count &&
        _tokens[index].Type is TokenType.Keyword or TokenType.Identifier or TokenType.Function &&
        string.Equals(_tokens[index].Text, word, StringComparison.OrdinalIgnoreCase);

    private bool UnsupportedConstruct(string construct, Lexeme start, Lexeme end)
    {
        if (Supports(construct)) { return false; }
        _diagnostics.Add(QueryDiagnostics.UnsupportedClause(construct, Profile.Language, Span(start, end)));
        return true;
    }

    private GqlQueryExpression EmptyExpression() => new([], null, [], [], false, [], Span(Current, Current));
    private Lexeme Current => _tokens[Math.Min(_position, _tokens.Count - 1)];
    private Lexeme Previous => _tokens[Math.Max(0, _position - 1)];
    private bool Failed => _diagnostics.Count != 0;
    private void Advance() { if (_position < _tokens.Count - 1) { _position++; } }
    private bool Is(string text) => Current.Type is not (TokenType.String or TokenType.QuotedIdentifier) &&
        string.Equals(Current.Text, text, StringComparison.OrdinalIgnoreCase);
    private bool Take(string text)
    {
        if (!Is(text)) { return false; }
        Advance();
        return true;
    }
    private bool Take(TokenType type)
    {
        if (Current.Type != type) { return false; }
        Advance();
        return true;
    }
    private void Expect(string text)
    {
        if (!Failed && !Take(text)) { Error("GQL0002", $"Expected {text}.", Current); }
    }
    private void Expect(TokenType type, string text)
    {
        // Preserve the original capability diagnostic instead of masking it with missing delimiters.
        if (!Failed && !Take(type)) { Error("GQL0002", $"Expected {text}.", Current); }
    }
    private static Location Span(Lexeme start, Lexeme end) =>
        Location.Create(start.Line, end.Line + end.Text.AsSpan().Count('\n'), start.Start, end.End);
    private string Identifier(bool allowKeyword = false)
    {
        var token = Current;
        if (Take(TokenType.QuotedIdentifier))
        {
            if (token.Text.Length <= 2) { Error("GQL0002", "An identifier cannot be empty.", token); }
            return token.Text.Length >= 2 ? token.Text[1..^1] : string.Empty;
        }
        if ((token.Type is TokenType.Identifier or TokenType.Function || allowKeyword && token.Type == TokenType.Keyword) &&
            token.Text.Length > 0 && (char.IsLetter(token.Text[0]) || token.Text[0] == '_'))
        {
            Advance();
            return token.Text;
        }
        Error("GQL0002", "Expected an identifier.", token);
        return string.Empty;
    }
    private void Unsupported(string clause, Lexeme token) =>
        _diagnostics.Add(QueryDiagnostics.UnsupportedClause(clause, Profile.Language, Span(token, token)));
    private void Error(string code, string message, Lexeme token) => _diagnostics.Add(new Diagnostic
    {
        Code = code, Message = message, Start = token.Start, End = token.End,
        Line = token.Line, Severity = DiagnosticSeverity.Error, Location = DiagnosticLocation.Absolute,
    });
    /// <summary>
    /// Names a character as written, or by code point when it is invisible or is a
    /// supplementary character (the lexer keeps a surrogate pair together as one token).
    /// </summary>
    private static string DescribeCharacter(string text)
    {
        if (text.Length == 2 && char.IsSurrogatePair(text[0], text[1]))
        {
            return $"U+{char.ConvertToUtf32(text[0], text[1]):X4}";
        }
        return char.IsControl(text[0]) || char.IsSurrogate(text[0]) ||
               char.GetUnicodeCategory(text[0]) is UnicodeCategory.Format
            ? $"U+{(int)text[0]:X4}"
            : $"'{text}'";
    }
    private static bool IsCompleteQuoted(string text)
    {
        if (text.Length < 2) { return false; }
        char quote = text[0];
        for (int i = 1; i < text.Length; i++)
        {
            if (text[i] != quote) { continue; }
            if (quote == '\'' && i + 1 < text.Length && text[i + 1] == quote) { i++; continue; }
            return i == text.Length - 1;
        }
        return false;
    }
    private static bool IsCompleteComment(string text)
    {
        if (!text.StartsWith("/*", StringComparison.Ordinal)) { return true; }
        int depth = 0;
        for (int i = 0; i + 1 < text.Length; i++)
        {
            if (text[i] == '/' && text[i + 1] == '*') { depth++; i++; }
            else if (text[i] == '*' && text[i + 1] == '/') { depth--; i++; }
        }
        return depth == 0;
    }
    private readonly record struct Lexeme(TokenType Type, string Text, int Start, int End, int Line);
}
