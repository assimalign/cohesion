using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using Assimalign.Cohesion.Database.Graph.Language.Internal;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>Parses the executable GQL subset using the shared lexer and analyzer pipeline.</summary>
/// <remarks>Concurrent calls are serialized. Malformed input returns statement diagnostics.</remarks>
public sealed partial class GqlQueryParser : QueryParser
{
    private readonly object _gate = new();
    private readonly List<Lexeme> _tokens = [];
    private readonly List<Diagnostic> _diagnostics = [];
    // Each '--' comment by its start offset, so a pattern element can tell whether one begins
    // exactly where its ')' or ']' ends (GQL0008).
    private readonly Dictionary<int, Lexeme> _lineComments = new();
    private string _source = string.Empty;
    private int _position;

    // A statement that nests deeper than the stack the parsing thread has left. The same text
    // parses on a thread with more stack: the statement is within the language, the thread is too
    // small for it, so GraphQueryRequest.FromGql reports it as the engine's statement-too-complex
    // failure, COHDBG007, not as a parse error.
    private const string statementTooDeepCode = "GQL0009";

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
        _lineComments.Clear();
        _position = 0;
        int line = 1;
        int scanned = 0;
        while (lexer.MoveNext())
        {
            var token = lexer.Current;
            // Lines break where the lexer ends a line comment (#1150). Counting each gap on its
            // own is exact: a token never starts with LF, so no CR LF straddles two gaps.
            line += TokenLexer.CountLineBreaks(_source.AsSpan(scanned, token.Position - scanned));
            scanned = token.Position;
            var lexeme = new Lexeme(token.Type, token.Value.ToString(), token.Position,
                token.Position + token.Value.Length, line);
            if (token.Type == TokenType.Comment)
            {
                if (!IsCompleteComment(lexeme.Text)) { Error("GQL0003", "Unterminated block comment.", lexeme); }
                else if (lexeme.Text.StartsWith("--", StringComparison.Ordinal)) { _lineComments[lexeme.Start] = lexeme; }
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
        line += TokenLexer.CountLineBreaks(_source.AsSpan(scanned));
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
    /// word only, a tilde edge reports its whole edge, and multi-word prefixes such as
    /// <c>ALL SHORTEST</c> and <c>NODETACH DELETE</c> are one construct. Label names, in a
    /// pattern or in a <c>WHERE</c> label expression, are never looked up as words.
    /// </summary>
    private void FindUnsupported()
    {
        int patternDepth = 0;
        bool inPredicateOrReturn = false;
        bool inPredicate = false;
        bool sawCall = false;
        var label = LabelScan.None;
        int labelParens = 0;
        for (int i = 0; i < _tokens.Count; i++)
        {
            var token = _tokens[i];
            if (token.Type == TokenType.Semicolon) { break; }
            if (token.Type == TokenType.LeftBrace && StartsQuantifier(i, patternDepth))
            {
                Unsupported("QUANTIFIED PATTERN", token);
            }
            if (token.Type is TokenType.LeftParen or TokenType.LeftBracket or TokenType.LeftBrace) { patternDepth++; }
            if (token.Type is TokenType.RightParen or TokenType.RightBracket or TokenType.RightBrace) { patternDepth--; }

            // A WHERE label expression (n:A|B, n IS [NOT] LABELED A|B) holds label names exactly
            // as a pattern does, so n:A|Order names a label, not ORDER BY.
            if (label != LabelScan.None)
            {
                if (ContinuesLabelExpression(token.Type, ref label, ref labelParens)) { continue; }
                label = LabelScan.None;
                labelParens = 0;
            }
            if (token.Type == TokenType.Colon && inPredicate && i > 0 &&
                _tokens[i - 1].Type is TokenType.Identifier or TokenType.QuotedIdentifier)
            {
                label = LabelScan.ExpectPrimary;
                continue;
            }
            // ':' or IS straight after an abbreviated edge (-IS T->, <-:A|B-) is a label
            // expression on an edge ISO gives no filler. The parser reports that (GQL0002); the
            // scan reads the names that follow as labels, so IS is not the unsupported IS clause
            // and -IS Order-> is not ORDER BY.
            if (patternDepth == 0 && !inPredicateOrReturn && FollowsEdgeConnector(i) &&
                (token.Type == TokenType.Colon || IsWordAt(i, "IS")))
            {
                label = LabelScan.ExpectPrimary;
                continue;
            }

            // A tilde edge right after a node pattern is undirected, which the engine cannot
            // store: ~[]~, <~[]~, ~[]~>, ~, <~ and ~> each name one construct.
            if (token.Type == TokenType.RightParen && patternDepth == 0 && !inPredicateOrReturn &&
                TryFindUndirectedEdge(i + 1, out int edgeEnd))
            {
                UnsupportedConstruct(GqlUnsupportedVocabulary.UndirectedEdge, _tokens[i + 1], _tokens[edgeEnd]);
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
            if (patternDepth == 0 && value is "WHERE" or "RETURN" or "MATCH" or "CREATE" or "INSERT" or "DELETE")
            {
                inPredicate = value == "WHERE";
            }

            // n IS [NOT] LABELED A in WHERE is ISO's <labeled predicate>; IS NULL and the truth
            // tests stay unsupported (gql-where-expr), and so does any IS in RETURN, whose subset
            // has no expressions. In a pattern, (n IS A) is a label expression and needs no case
            // here: words inside a pattern are names.
            if (value == "IS" && inPredicate)
            {
                int labeled = IsWordAt(i + 1, "NOT") ? i + 2 : i + 1;
                if (IsWordAt(labeled, GqlLabelVocabulary.Labeled))
                {
                    i = labeled;
                    label = LabelScan.ExpectPrimary;
                    continue;
                }
            }
            if (inPredicateOrReturn && i + 1 < _tokens.Count && _tokens[i + 1].Type == TokenType.LeftParen &&
                value is not ("WHERE" or "AND" or "RETURN"))
            {
                Unsupported($"FUNCTION {token.Text}", token);
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
                case GqlWordPosition.EdgePattern:
                    continue;
                default:
                    if (value is "ALL" or "ANY" or "SHORTEST" && !inPredicateOrReturn &&
                        TryPathSearchPrefix(i, out string prefix, out int last))
                    {
                        // One path search prefix, with its path mode and PATH/PATHS, not ALL
                        // (or ANY) followed by SHORTEST PATH or a separate path mode.
                        construct = prefix;
                        i = last;
                        end = _tokens[last];
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

    /// <summary>
    /// Reads an ISO/IEC 39075 path search prefix at <paramref name="index"/>: <c>ALL SHORTEST</c>,
    /// <c>ANY SHORTEST</c> or <c>SHORTEST [k]</c>, or <c>ALL</c> or <c>ANY [k]</c> directly before
    /// a path pattern. Each takes an optional path mode and <c>PATH</c>/<c>PATHS</c>, and
    /// <c>SHORTEST</c> takes <c>GROUP</c>/<c>GROUPS</c> instead; all of it is one construct.
    /// </summary>
    /// <param name="index">The index of <c>ALL</c>, <c>ANY</c> or <c>SHORTEST</c>.</param>
    /// <param name="construct">The construct the prefix names.</param>
    /// <param name="last">The index of the prefix's last token.</param>
    /// <returns><see langword="true"/> when a prefix starts at <paramref name="index"/>.</returns>
    private bool TryPathSearchPrefix(int index, out string construct, out int last)
    {
        string head = _tokens[index].Text.ToUpperInvariant();
        int next = index + 1;
        bool shortest = head == "SHORTEST" || IsWordAt(next, "SHORTEST");
        if (head != "SHORTEST" && shortest) { next++; }
        if (head != "ALL" && next < _tokens.Count && _tokens[next].Type == TokenType.Integer) { next++; }

        if (next < _tokens.Count && _tokens[next].Type is TokenType.Identifier or TokenType.Keyword &&
            GqlUnsupportedVocabulary.TryFind(_tokens[next].Text.ToUpperInvariant(), out var mode) &&
            mode.Position == GqlWordPosition.PathMode)
        {
            next++;
        }
        if (IsWordAt(next, "PATH") || IsWordAt(next, "PATHS") ||
            head == "SHORTEST" && (IsWordAt(next, "GROUP") || IsWordAt(next, "GROUPS")))
        {
            next++;
        }

        last = next - 1;
        if (shortest)
        {
            construct = head == "SHORTEST" ? GqlClauses.ShortestPath : head + " SHORTEST";
            return true;
        }

        construct = head + " PATH SEARCH";
        return next < _tokens.Count && _tokens[next].Type == TokenType.LeftParen;
    }

    /// <summary>
    /// Whether the <c>{</c> at <paramref name="index"/> opens an ISO quantifier (<c>{m,n}</c>,
    /// <c>{m}</c>, <c>{,n}</c>) after an edge or a node pattern. A quantifier starts with a bound
    /// or a comma. A property map starts with a key, as in <c>(n:(A|B) {k: 1})</c> or the
    /// malformed abbreviated edge <c>-{k: 1}-&gt;</c>, and is left to the parser.
    /// </summary>
    /// <param name="index">The index of the <c>{</c>.</param>
    /// <param name="patternDepth">The nesting depth before the <c>{</c>.</param>
    private bool StartsQuantifier(int index, int patternDepth)
    {
        if (index == 0 || index + 1 >= _tokens.Count ||
            _tokens[index + 1].Type is not (TokenType.Integer or TokenType.Comma))
        {
            return false;
        }

        return _tokens[index - 1].Type switch
        {
            TokenType.RightArrow or TokenType.LeftArrow or TokenType.Minus => true,
            // A ')' inside an element closes a label group, not the node pattern.
            TokenType.RightParen => patternDepth == 0,
            // The '>' of '<->'.
            TokenType.GreaterThan => index >= 2 && _tokens[index - 2].Type == TokenType.LeftArrow && Adjacent(index - 2, index - 1),
            _ => false,
        };
    }

    /// <summary>
    /// Reads an ISO/IEC 39075 tilde edge starting at <paramref name="index"/>, directly after a
    /// node pattern: <c>~</c> or <c>&lt;~</c> (no space inside), through a bracketed form's
    /// closing <c>~</c> or <c>~&gt;</c>, or the abbreviated <c>~&gt;</c>.
    /// </summary>
    /// <param name="index">The index after the node pattern's <c>)</c>.</param>
    /// <param name="last">
    /// The index of the edge's last token: its closing tilde, or its opening tilde when a
    /// bracketed form does not close with one.
    /// </param>
    /// <returns><see langword="true"/> when a tilde edge starts at <paramref name="index"/>.</returns>
    private bool TryFindUndirectedEdge(int index, out int last)
    {
        last = index;
        if (index >= _tokens.Count) { return false; }
        int tilde = index;
        if (_tokens[index].Type == TokenType.LessThan)
        {
            tilde = index + 1;
            if (!Adjacent(index, tilde) || _tokens[tilde].Type != TokenType.Tilde) { return false; }
        }
        else if (_tokens[index].Type != TokenType.Tilde) { return false; }

        last = tilde;
        int next = tilde + 1;
        if (next < _tokens.Count && _tokens[next].Type == TokenType.LeftBracket)
        {
            int depth = 0;
            for (int j = next; j < _tokens.Count && _tokens[j].Type is not (TokenType.Semicolon or TokenType.Eof); j++)
            {
                if (_tokens[j].Type == TokenType.LeftBracket) { depth++; }
                else if (_tokens[j].Type == TokenType.RightBracket && --depth == 0)
                {
                    if (j + 1 < _tokens.Count && _tokens[j + 1].Type == TokenType.Tilde)
                    {
                        last = Adjacent(j + 1, j + 2) && _tokens[j + 2].Type == TokenType.GreaterThan ? j + 2 : j + 1;
                    }
                    break;
                }
            }
        }
        else if (Adjacent(tilde, next) && _tokens[next].Type == TokenType.GreaterThan)
        {
            last = next;
        }
        return true;
    }

    /// <summary>
    /// Whether the token at <paramref name="index"/> follows an edge's connector at pattern depth
    /// zero: <c>-</c>, <c>&lt;-</c>, <c>-&gt;</c>, or the <c>&gt;</c> of a touching <c>&lt;-&gt;</c>.
    /// Outside <c>WHERE</c> and <c>RETURN</c> those tokens occur only between pattern elements.
    /// </summary>
    private bool FollowsEdgeConnector(int index)
    {
        if (index == 0) { return false; }
        return _tokens[index - 1].Type switch
        {
            TokenType.Minus or TokenType.LeftArrow or TokenType.RightArrow => true,
            TokenType.GreaterThan => index >= 2 && _tokens[index - 2].Type == TokenType.LeftArrow && Adjacent(index - 2, index - 1),
            _ => false,
        };
    }

    /// <summary>Whether two tokens touch, with no whitespace or comment between them.</summary>
    private bool Adjacent(int left, int right) =>
        right < _tokens.Count && _tokens[left].End == _tokens[right].Start;

    /// <summary>
    /// Advances the capability scan through a <c>WHERE</c> label expression: primaries (a name,
    /// <c>%</c> or a parenthesized expression), each optionally negated, joined by <c>|</c> or
    /// <c>&amp;</c>.
    /// </summary>
    /// <param name="type">The token's type.</param>
    /// <param name="state">The scan's position in the expression.</param>
    /// <param name="parens">The open label parentheses.</param>
    /// <returns><see langword="true"/> while the token belongs to the expression.</returns>
    private static bool ContinuesLabelExpression(TokenType type, ref LabelScan state, ref int parens)
    {
        if (state == LabelScan.ExpectPrimary)
        {
            switch (type)
            {
                case TokenType.Identifier or TokenType.Keyword or TokenType.Function or TokenType.QuotedIdentifier or TokenType.Percent:
                    state = LabelScan.AfterPrimary;
                    return true;
                case TokenType.Bang:
                    return true;
                case TokenType.LeftParen:
                    parens++;
                    return true;
                default:
                    return false;
            }
        }

        switch (type)
        {
            case TokenType.Pipe or TokenType.Ampersand:
                state = LabelScan.ExpectPrimary;
                return true;
            case TokenType.RightParen when parens > 0:
                parens--;
                return true;
            default:
                return false;
        }
    }

    /// <summary>Where the capability scan is inside a <c>WHERE</c> label expression.</summary>
    private enum LabelScan
    {
        /// <summary>Outside a label expression.</summary>
        None,
        /// <summary>After <c>:</c>, <c>LABELED</c>, an operator, <c>!</c> or <c>(</c>.</summary>
        ExpectPrimary,
        /// <summary>After a name, <c>%</c> or a closing <c>)</c>.</summary>
        AfterPrimary,
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
        Location.Create(start.Line, end.Line + TokenLexer.CountLineBreaks(end.Text), start.Start, end.End);
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
    /// <summary>
    /// Whether the parser may descend into the parenthesized group that <paramref name="token"/>
    /// opens. The grammar recurses only through parentheses, in a label expression or a predicate,
    /// and no fixed depth applies (Neo4j bounds this nesting only by the stack too). When the
    /// thread is out of stack, the parse stops with <c>GQL0009</c> at that <c>(</c>, and every rule
    /// still on the stack returns without reading further, instead of the process overflowing.
    /// </summary>
    /// <param name="token">The <c>(</c> about to be consumed.</param>
    /// <returns><see langword="true"/> when the thread has stack left to parse the group.</returns>
    private bool HasStackToNest(Lexeme token)
    {
        if (RuntimeHelpers.TryEnsureSufficientExecutionStack()) { return true; }
        Error(statementTooDeepCode, "The statement nests deeper than the stack available to the parser on this thread; " +
            "reduce the nesting of its parentheses or run it on a thread with a larger stack.", token);
        return false;
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
