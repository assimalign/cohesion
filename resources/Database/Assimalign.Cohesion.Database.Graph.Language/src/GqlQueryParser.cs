using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using Assimalign.Cohesion.Database.Graph.Language.Internal;
using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>Parses the executable GQL subset using the shared lexer and analyzer pipeline.</summary>
/// <remarks>Concurrent calls are serialized. Malformed input returns statement diagnostics.</remarks>
[global::System.Diagnostics.DebuggerDisplay("GqlQueryParser")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Graph.Language.GqlQueryParser.DebugView))]
public sealed partial class GqlQueryParser : QueryParser
{
    private readonly bool _allowCustomFunctions;
    private readonly object _gate = new();
    private readonly List<Lexeme> _tokens = [];
    private readonly List<Diagnostic> _diagnostics = [];
    private readonly HashSet<int> _parameterFormPositions = [];
    private readonly HashSet<int> _questionedPathPositions = [];
    // Each '--' comment by its start offset, so a pattern element can tell whether one begins
    // exactly where its ')' or ']' ends (CG00008).
    private readonly Dictionary<int, Lexeme> _lineComments = new();
    private string _source = string.Empty;
    private int _position;
    private int _collectionBar = -1;

    // A statement that nests deeper than the stack the parsing thread has left. The same text
    // parses on a thread with more stack: the statement is within the language, the thread is too
    // small for it, so GraphQueryRequest.FromGql reports it as the engine's statement-too-complex
    // failure, CG05008, not as a parse error.
    private const string statementTooDeepCode = "CG00009";

    /// <summary>Initializes a GQL parser.</summary>
    /// <param name="options">Optional shared analyzer configuration.</param>
    public GqlQueryParser(QueryParserOptions? options = null) : this(false, options) { }

    /// <summary>Initializes a parser which can defer named function resolution to an engine.</summary>
    /// <param name="allowCustomFunctions">Whether ordinary named calls are admitted.</param>
    /// <param name="options">Optional shared analyzer configuration.</param>
    public GqlQueryParser(bool allowCustomFunctions, QueryParserOptions? options = null) : base(options ?? new QueryParserOptions())
    { _allowCustomFunctions = allowCustomFunctions; }

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
        _parameterFormPositions.Clear();
        _questionedPathPositions.Clear();
        _lineComments.Clear();
        _position = 0;
        _collectionBar = -1;
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
                if (!IsCompleteComment(lexeme.Text)) { Error("CG00003", ErrorMessages.GqlQueryParserParseCoreVoid, lexeme); }
                else if (lexeme.Text.StartsWith("--", StringComparison.Ordinal)) { _lineComments[lexeme.Start] = lexeme; }
            }
            else
            {
                // GQL's D3 merge operator is one token, without changing SQL/OQL lexing.
                if (token.Type == TokenType.Equals && _tokens.Count > 0 && _tokens[^1] is { Type: TokenType.Plus } plus && plus.End == lexeme.Start)
                { _tokens[^1] = plus with { Type = TokenType.Unrecognized, Text = "+=", End = lexeme.End }; }
                else { _tokens.Add(lexeme); }
                if (token.Type is TokenType.String or TokenType.QuotedIdentifier && !IsCompleteQuoted(lexeme.Text))
                {
                    Error("CG00003", ErrorMessages.GqlQueryParserParseCoreVoid2, lexeme);
                }
                else if (token.Type == TokenType.Unrecognized && token.Value[0] != '?')
                {
                    // A character GQL does not use is never a name or an expression, so the
                    // statement is not parsed and nothing is bound (#1101).
                    Error("CG00002", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.GqlQueryParserParseCoreVoid3, DescribeCharacter(lexeme.Text)), lexeme);
                }
            }
        }
        line += TokenLexer.CountLineBreaks(_source.AsSpan(scanned));
        _tokens.Add(new Lexeme(TokenType.Eof, string.Empty, _source.Length, _source.Length, line));

        // SHOW has a deliberately separate grammar: catalog definitions are not graph elements.
        // Check attempted composition before generic capability errors (SET, DROP, etc.).
        bool catalogStatement = Is("SHOW");
        bool transactionStatement = Is(GqlTransactionVocabulary.Start) || Is(GqlTransactionVocabulary.Begin) ||
            Is(GqlTransactionVocabulary.Commit) || Is(GqlTransactionVocabulary.Rollback);
        if (catalogStatement) { FindCatalogMutation(); }
        else if (!transactionStatement) { FindUnsupported(); }
        GqlQueryExpression expression;
        if (Failed) { expression = EmptyExpression(); }
        else if (Current.Type == TokenType.Eof)
        {
            Error("CG00001", ErrorMessages.GqlQueryParserParseCoreVoid4, Current);
            expression = EmptyExpression();
        }
        else if (transactionStatement)
        {
            var first = Current;
            var command = Is(GqlTransactionVocabulary.Start) || Is(GqlTransactionVocabulary.Begin) ? GqlTransactionCommand.Start
                : Is(GqlTransactionVocabulary.Commit) ? GqlTransactionCommand.Commit : GqlTransactionCommand.Rollback;
            _position++;
            if (first.Text.Equals(GqlTransactionVocabulary.Start, StringComparison.OrdinalIgnoreCase))
            { Expect(GqlTransactionVocabulary.Transaction); }
            else if (first.Text.Equals(GqlTransactionVocabulary.Begin, StringComparison.OrdinalIgnoreCase))
            { Take(GqlTransactionVocabulary.Transaction); }
            expression = new GqlQueryExpression(Array.Empty<GqlStage>(), Span(first, _tokens[_position - 1]))
            { TransactionCommand = command };
        }
        else if (Is("EXPLAIN"))
        {
            _position++;
            if (Current.Type == TokenType.LeftParen && IsWordAt(_position + 1, "FORMAT"))
            {
                Unsupported("EXPLAIN OPTIONS", Current);
                expression = EmptyExpression();
            }
            else if (IsQueryStart() || Is("NEXT") || Current.Type == TokenType.LeftParen)
            {
                expression = ParseNextQuery();
                expression.Explain();
            }
            else
            {
                Unsupported($"EXPLAIN {Current.Text}", Current);
                expression = EmptyExpression();
            }
        }
        else if (catalogStatement) { expression = ParseCatalog(); }
        else if (IsQueryStart() || Is("NEXT") || Current.Type == TokenType.LeftParen) { expression = ParseNextQuery(); }
        else
        {
            if (Is("ORDER") || Is("OFFSET") || Is("SKIP") || Is("LIMIT"))
            {
                Unsupported("STANDALONE ORDER BY AND PAGE STATEMENT", Current);
            }
            else if (Is("DELETE") || Is("DETACH") || Is("WHERE") || Is("SET") || Is("REMOVE"))
            {
                Error("CG00002", ErrorMessages.GqlQueryParserParseCoreVoid5, Current);
            }
            else { Unsupported(Current.Text, Current); }
            expression = EmptyExpression();
        }

        if (!Failed)
        {
            Take(TokenType.Semicolon);
            if (Current.Type != TokenType.Eof)
            {
                Error("CG00002", ErrorMessages.GqlQueryParserParseCoreVoid6, Current);
            }
        }
        // '?' still binds nothing. Only an operand receives the parameter-form message;
        // names, paths and trailing positions keep lexical strictness's generic message.
        foreach (var token in _tokens)
        {
            if (token.Type != TokenType.Unrecognized || token.Text != "?") { continue; }
            if (_questionedPathPositions.Contains(token.Start)) { continue; }
            if (!_parameterFormPositions.Contains(token.Start))
            {
                _diagnostics.RemoveAll(diagnostic => diagnostic.Start == token.Start);
                Error("CG00002", ErrorMessages.GqlQueryParserParseCoreVoid7, token);
            }
            expression = EmptyExpression();
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
    private void FindUnsupported(int start = 0, int? endExclusive = null, bool existsPattern = false)
    {
        int patternDepth = 0;
        int mapDepth = 0;
        int inlinePredicateDepth = -1;
        var containers = new Stack<TokenType>();
        int mapSeparatorPosition = -1;
        bool inValueExpression = false;
        bool inPattern = existsPattern;
        bool sawCall = false;
        bool inUpdate = false;
        var label = LabelScan.None;
        int labelParens = 0;
        for (int i = start; i < (endExclusive ?? _tokens.Count); i++)
        {
            var token = _tokens[i];
            if (token.Type == TokenType.Semicolon) { break; }
            // Query groups start only in operand slots. Scan each body with fresh clause,
            // value, label and update context, relative to its own delimiter depth.
            if (!existsPattern && patternDepth == 0 && token.Type == TokenType.LeftParen &&
                (i == start || i == start + 1 && IsWordAt(start, "EXPLAIN") ||
                 IsConjunctionAt(i - 1) || IsWordAt(i - 1, "NEXT") ||
                 i > 1 && IsConjunctionAt(i - 2) && (IsWordAt(i - 1, "ALL") || IsWordAt(i - 1, "DISTINCT"))))
            {
                if (!HasStackToNest(token)) { return; }
                int body = i + 1;
                int depth = 1;
                while (++i < (endExclusive ?? _tokens.Count) && _tokens[i].Type != TokenType.Eof)
                {
                    if (_tokens[i].Type == TokenType.LeftParen) { depth++; }
                    if (_tokens[i].Type == TokenType.RightParen && --depth == 0) { break; }
                }
                FindUnsupported(body, i);
                continue;
            }
            bool mapSeparator = token.Type == TokenType.Colon && containers.TryPeek(out var container) && container == TokenType.LeftBrace &&
                i > 1 && _tokens[i - 2].Type is TokenType.LeftBrace or TokenType.Comma;
            if (mapSeparator) { mapSeparatorPosition = i; }
            if (token.Type is TokenType.LeftParen or TokenType.LeftBracket or TokenType.LeftBrace) { patternDepth++; containers.Push(token.Type); }
            if (token.Type is TokenType.RightParen or TokenType.RightBracket or TokenType.RightBrace) { patternDepth--; containers.TryPop(out _); }

            if (token.Type == TokenType.LeftBrace) { mapDepth++; }
            if (token.Type == TokenType.RightBrace) { mapDepth--; }
            if (patternDepth < inlinePredicateDepth) { inlinePredicateDepth = -1; }
            bool valuePosition = inValueExpression || mapDepth > 0 || inlinePredicateDepth >= 0;

            // A WHERE/RETURN label expression (n:A|B, n IS [NOT] LABELED A|B) holds label names exactly
            // as a pattern does, so n:A|Order names a label, not ORDER BY.
            if (label != LabelScan.None)
            {
                if (ContinuesLabelExpression(token.Type, ref label, ref labelParens)) { continue; }
                label = LabelScan.None;
                labelParens = 0;
            }
            if (token.Type == TokenType.Colon && valuePosition && i > 0 &&
                !mapSeparator &&
                IsVariableLexeme(_tokens[i - 1]))
            {
                label = LabelScan.ExpectPrimary;
                continue;
            }
            // ':' or IS straight after an abbreviated edge (-IS T->, <-:A|B-) is a label
            // expression on an edge ISO gives no filler. The parser reports that (CG00002); the
            // scan reads the names that follow as labels, so IS is not the unsupported IS clause
            // and -IS Order-> is not ORDER BY.
            if (patternDepth == 0 && !valuePosition && FollowsEdgeConnector(i) &&
                (token.Type == TokenType.Colon || IsWordAt(i, "IS")))
            {
                label = LabelScan.ExpectPrimary;
                continue;
            }

            // A tilde edge right after a node pattern is undirected, which the engine cannot
            // store: ~[]~, <~[]~, ~[]~>, ~, <~ and ~> each name one construct.
            if (token.Type == TokenType.RightParen && patternDepth == 0 && !valuePosition &&
                TryFindUndirectedEdge(i + 1, out int edgeEnd))
            {
                UnsupportedConstruct(GqlUnsupportedVocabulary.UndirectedEdge, _tokens[i + 1], _tokens[edgeEnd]);
                continue;
            }

            if (token.Type is TokenType.Asterisk or TokenType.DotDot && !valuePosition && !inPattern)
            {
                Unsupported("QUANTIFIED PATTERN OR STAR PROJECTION", token);
                continue;
            }
            if (token.Type == TokenType.Tilde && valuePosition && i > 0 && _tokens[i - 1].Type == TokenType.Equals && Adjacent(i - 1, i))
            {
                Unsupported("REGULAR EXPRESSION =~", token);
                continue;
            }
            if (token.Type == TokenType.Parameter && token.Text == "$" &&
                i + 1 < _tokens.Count && _tokens[i + 1].Type == TokenType.Parameter && Adjacent(i, i + 1))
            {
                UnsupportedConstruct("SUBSTITUTED PARAMETER REFERENCE", token, _tokens[++i]);
                continue;
            }
            if (token.Type is not (TokenType.Keyword or TokenType.Identifier or TokenType.Function)) { continue; }
            // Contextual clause words, such as NEXT, remain legal aliases after AS.
            if (i > 0 && IsWordAt(i - 1, "AS") && (token.Type is TokenType.Identifier or TokenType.Function || token.Type == TokenType.Keyword && token.Text.ToUpperInvariant() is "NEXT" or "FOR")) { continue; }
            if (i > 0 && (_tokens[i - 1].Type == TokenType.Dot ||
                _tokens[i - 1].Type == TokenType.Colon && i - 1 != mapSeparatorPosition)) { continue; }
            if (i + 1 < _tokens.Count && _tokens[i + 1].Type == TokenType.Colon) { continue; }
            string value = token.Text.ToUpperInvariant();
            if (valuePosition && (value is "WALK" or "TRAIL" or "SIMPLE" or "ACYCLIC" or "ANY" or "ALL" or "SHORTEST") &&
                TryPatternComprehensionPrefix(i, out int patternPrefixEnd))
            {
                UnsupportedConstruct("PATTERN COMPREHENSION PATH PREFIX", token, _tokens[patternPrefixEnd]);
                i = patternPrefixEnd;
                continue;
            }
            // Scan the body in its own pattern context. Its WHERE and inline predicates use
            // exactly the ordinary capability checks; pattern identifiers remain names.
            if (valuePosition && value == "EXISTS" && i + 1 < _tokens.Count && _tokens[i + 1].Type == TokenType.LeftBrace)
            {
                if (!HasStackToNest(token)) { return; }
                int body = i + 2;
                int depth = 0;
                for (i++; i < _tokens.Count; i++)
                {
                    if (_tokens[i].Type == TokenType.LeftBrace) { depth++; }
                    if (_tokens[i].Type == TokenType.RightBrace && --depth == 0) { break; }
                    if (_tokens[i].Type == TokenType.Eof) { break; }
                }
                FindUnsupported(body, i, existsPattern: true);
                continue;
            }
            if (value == "WHERE" && inPattern && patternDepth > 0)
            { inlinePredicateDepth = patternDepth; continue; }
            // MATCH owns selector validation, including malformed counts and nested prefixes.
            if (inPattern && !valuePosition && value is "ALL" or "ANY" or "SHORTEST")
            {
                if (TryPathSearchPrefix(i, out _, out int selectorEnd)) { i = selectorEnd; }
                continue;
            }
            // ISO OPTIONAL block operands are recognized outside T1. Consume their complete
            // operand here so nested MATCH/WHERE words cannot produce extra capability errors.
            // This precedes function detection, including after a WHERE expression.
            if (patternDepth == 0 && value == "OPTIONAL")
            {
                if (i + 1 < _tokens.Count && _tokens[i + 1].Type is TokenType.LeftBrace or TokenType.LeftParen)
                {
                    int endIndex = i + 1;
                    int depth = 0;
                    for (int j = i + 1; j < _tokens.Count; j++)
                    {
                        if (_tokens[j].Type is TokenType.LeftBrace or TokenType.LeftParen or TokenType.LeftBracket) { depth++; }
                        if (_tokens[j].Type is TokenType.RightBrace or TokenType.RightParen or TokenType.RightBracket) { depth--; }
                        endIndex = j;
                        if (depth == 0 || _tokens[j].Type == TokenType.Eof) { break; }
                    }
                    UnsupportedConstruct(GqlUnsupportedVocabulary.OptionalBlockOperand, token, _tokens[endIndex]);
                    i = endIndex;
                }
                continue;
            }
            if (patternDepth == 0 && value is "WHERE" or "RETURN" or "WITH" or "LET" or "FILTER" or "FOR" or "UNWIND" or "SET" or "REMOVE") { inValueExpression = true; inPattern = false; }
            if (patternDepth == 0 && value is "MATCH" or "RETURN" or "WITH" or "LET" or "FILTER" or "FOR" or "UNWIND" or "INSERT" or "CREATE" or "MERGE" or "NEXT") { inUpdate = false; }
            if (patternDepth == 0 && value is "SET" or "REMOVE") { inUpdate = true; }
            if (patternDepth == 0 && (value is "MATCH" or "CREATE" or "INSERT" or "MERGE" or "DELETE" || value == "NEXT" && IsQueryStartAt(i + 1))) { inValueExpression = false; }
            if (patternDepth == 0 && value is "MATCH" or "CREATE" or "INSERT" or "MERGE") { inPattern = true; }
            if (patternDepth == 0 && value == "DELETE") { inPattern = false; }

            // n IS [NOT] LABELED A in WHERE is ISO's <labeled predicate>; IS NULL and truth
            // tests are scalar predicates (#1140), also available in RETURN (#1141).
            // In a pattern, (n IS A) is a label expression and needs no case
            // here: words inside a pattern are names.
            if (value == "IS" && valuePosition)
            {
                if (inUpdate && i > 1 && IsVariableLexeme(_tokens[i - 1]) &&
                    (IsWordAt(i - 2, "SET") || IsWordAt(i - 2, "REMOVE") || _tokens[i - 2].Type == TokenType.Comma))
                { label = LabelScan.ExpectPrimary; continue; }
                int labeled = IsWordAt(i + 1, "NOT") ? i + 2 : i + 1;
                if (IsWordAt(labeled, GqlLabelVocabulary.Labeled))
                {
                    i = labeled;
                    label = LabelScan.ExpectPrimary;
                    continue;
                }
            }
            if (valuePosition && token.Type is TokenType.Identifier or TokenType.Function &&
                i + 1 < _tokens.Count && _tokens[i + 1].Type == TokenType.LeftParen &&
                !(IsConjunctionAt(i) && FollowsCompositeValue(i)) &&
                !GqlUnsupportedVocabulary.TryFind(value, out _) && token.Type != TokenType.Function && !_allowCustomFunctions)
            {
                Error("CG00010", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.GqlQueryParserFindUnsupportedVoid, token.Text), token);
                continue;
            }
            if (value == "WITH" && valuePosition && i > 0 && (IsWordAt(i - 1, "STARTS") || IsWordAt(i - 1, "ENDS"))) { continue; }
            if (value is "ALL" or "ANY" or "NONE" or "SINGLE" && valuePosition &&
                i + 1 < _tokens.Count && _tokens[i + 1].Type == TokenType.LeftParen) { continue; }
            if (value == "ALL" && i > 0 && (IsConjunctionAt(i - 1) || IsWordAt(i - 1, "RETURN") || IsWordAt(i - 1, "WITH") || _tokens[i - 1].Type == TokenType.LeftParen && i > 1 && (_tokens[i - 2].Type == TokenType.Function || _allowCustomFunctions && _tokens[i - 2].Type == TokenType.Identifier))) { continue; }
            if (patternDepth != 0 && !valuePosition) { continue; }
            if (!GqlUnsupportedVocabulary.TryFind(value, out var word)) { continue; }

            bool first = i == start;
            var end = token;
            string construct = word.Construct;
            switch (word.Position)
            {
                case GqlWordPosition.FunctionCall:
                    if (!valuePosition || i + 1 >= _tokens.Count || _tokens[i + 1].Type != TokenType.LeftParen) { continue; }
                    break;
                case GqlWordPosition.ValueExpression:
                    // ParseOperand owns niladic expressions so binding sites fail as syntax.
                    continue;
                case GqlWordPosition.BeforeDelete:
                    if (!IsWordAt(i + 1, "DELETE")) { continue; }
                    end = _tokens[i + 1];
                    break;
                case GqlWordPosition.EdgePattern:
                    continue;
                default:
                    if (value is "ALL" or "ANY" or "SHORTEST" && !valuePosition &&
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
    /// <c>SHORTEST</c> may then take <c>GROUP</c>/<c>GROUPS</c>; all of it is one construct.
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
        if (head != "ALL" && next < _tokens.Count && _tokens[next].Type is TokenType.Integer or TokenType.Parameter) { next++; }

        if (TryPathModeAt(next, out _))
        {
            next++;
        }
        if (IsWordAt(next, "PATH") || IsWordAt(next, "PATHS"))
        {
            next++;
        }
        if (head == "SHORTEST" && (IsWordAt(next, "GROUP") || IsWordAt(next, "GROUPS"))) { next++; }

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

    private bool IsQueryStart() => Is("MATCH") || Is("OPTIONAL") || Is("CREATE") || Is("INSERT") || Is("MERGE") || Is("RETURN") ||
        Is("LET") || Is("FILTER") || Is("FOR") || Is("UNWIND") || Is("WITH");

    private bool IsConjunctionAt(int index) => index >= 0 &&
        (IsWordAt(index, "UNION") || IsWordAt(index, "EXCEPT") || IsWordAt(index, "INTERSECT") || IsWordAt(index, "OTHERWISE"));

    // Contextual conjunctions followed by a grouped operand are not function calls.
    private bool FollowsCompositeValue(int index) => index > 0 &&
        (_tokens[index - 1].Type is TokenType.Identifier or TokenType.Function or TokenType.QuotedIdentifier or
            TokenType.Integer or TokenType.Float or TokenType.String or TokenType.Parameter or
            TokenType.RightParen or TokenType.RightBracket or TokenType.RightBrace ||
        IsWordAt(index - 1, "NULL") || IsWordAt(index - 1, "TRUE") || IsWordAt(index - 1, "FALSE") ||
        IsWordAt(index - 1, "END") || IsWordAt(index - 1, "ASC") || IsWordAt(index - 1, "DESC") ||
        IsWordAt(index - 1, "ASCENDING") || IsWordAt(index - 1, "DESCENDING") ||
        IsWordAt(index - 1, "FIRST") || IsWordAt(index - 1, "LAST"));

    private bool IsVariableToken => IsVariableLexeme(Current);
    private static bool IsVariableLexeme(Lexeme token) => !IsReservedTemporal(token) &&
        (token.Type is TokenType.Identifier or TokenType.Function or TokenType.QuotedIdentifier ||
        token.Type == TokenType.Keyword && token.Text.ToUpperInvariant() is "NEXT" or "FOR");
    private static bool IsReservedTemporal(Lexeme token) => token.Type is not (TokenType.String or TokenType.QuotedIdentifier) &&
        token.Text.ToUpperInvariant() is "CURRENT_DATE" or "CURRENT_TIMESTAMP" or "CURRENT_TIME" or "LOCAL_TIME" or "LOCAL_TIMESTAMP";
    private bool IsQueryStartAt(int index) => IsWordAt(index, "MATCH") || IsWordAt(index, "OPTIONAL") || IsWordAt(index, "CREATE") ||
        IsWordAt(index, "INSERT") || IsWordAt(index, "MERGE") || IsWordAt(index, "RETURN") || IsWordAt(index, "LET") || IsWordAt(index, "FILTER") ||
        IsWordAt(index, "FOR") || IsWordAt(index, "UNWIND") || IsWordAt(index, "WITH");

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
        if (!Failed && !Take(text)) { Error("CG00002", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.GqlQueryParserExpectVoid, text), Current); }
    }
    private void Expect(TokenType type, string text)
    {
        // Preserve the original capability diagnostic instead of masking it with missing delimiters.
        if (!Failed && !Take(type)) { Error("CG00002", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.GqlQueryParserExpectVoid, text), Current); }
    }
    private static Location Span(Lexeme start, Lexeme end) =>
        Location.Create(start.Line, end.Line + TokenLexer.CountLineBreaks(end.Text), start.Start, end.End);
    private string Identifier(bool allowKeyword = false)
    {
        var token = Current;
        if (Take(TokenType.QuotedIdentifier))
        {
            if (token.Text.Length <= 2) { Error("CG00002", ErrorMessages.GqlQueryParserIdentifierVoid, token); }
            return token.Text.Length >= 2 ? token.Text[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal) : string.Empty;
        }
        if ((allowKeyword || !IsReservedTemporal(token)) && (token.Type is TokenType.Identifier or TokenType.Function || token.Type == TokenType.Keyword && token.Text.ToUpperInvariant() is "NEXT" or "FOR" || allowKeyword && token.Type == TokenType.Keyword) &&
            token.Text.Length > 0 && (char.IsLetter(token.Text[0]) || token.Text[0] == '_'))
        {
            Advance();
            return token.Text;
        }
        Error("CG00002", ErrorMessages.GqlQueryParserIdentifierVoid2, token);
        return string.Empty;
    }
    /// <summary>
    /// Whether the parser may descend into the parenthesized group that <paramref name="token"/>
    /// opens. The grammar recurses only through parentheses, in a label expression or a predicate,
    /// and no fixed depth applies (Neo4j bounds this nesting only by the stack too). When the
    /// thread is out of stack, the parse stops with <c>CG00009</c> at that <c>(</c>, and every rule
    /// still on the stack returns without reading further, instead of the process overflowing.
    /// </summary>
    /// <param name="token">The <c>(</c> about to be consumed.</param>
    /// <returns><see langword="true"/> when the thread has stack left to parse the group.</returns>
    private bool HasStackToNest(Lexeme token)
    {
        if (RuntimeHelpers.TryEnsureSufficientExecutionStack()) { return true; }
        Error(statementTooDeepCode, ErrorMessages.GqlQueryParserHasStackToNestVoid, token);
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
            if (i + 1 < text.Length && text[i + 1] == quote) { i++; continue; }
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

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Graph.Language.GqlQueryParser value)
        {
            _ = value;
        }
    }
}
