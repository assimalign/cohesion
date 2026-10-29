using System;
using System.Collections.Generic;
using System.Globalization;
using Assimalign.Cohesion.Database.Documents.Language.Internal;
using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>Parses the executable OQL profile into an expression tree with diagnostics.</summary>
/// <remarks>Instances serialize concurrent calls. Offsets are zero-based UTF-16 positions with exclusive ends.</remarks>
[global::System.Diagnostics.DebuggerDisplay("OqlQueryParser")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Documents.Language.OqlQueryParser.DebugView))]
public sealed partial class OqlQueryParser : QueryParser
{
    private readonly object _gate = new();
    private readonly bool _allowUnregisteredFunctions;
    private string _source = string.Empty;
    private readonly List<Lexeme> _tokens = [];
    private readonly List<Diagnostic> _diagnostics = [];
    private readonly HashSet<int> _parameterFormPositions = [];
    private bool[] _comprehensionTails = [];
    private int _position;
    private int _depth;

    /// <summary>Initializes an OQL parser.</summary>
    /// <param name="options">Optional shared analyzer configuration.</param>
    public OqlQueryParser(QueryParserOptions? options = null) : this(false, options) { }

    /// <summary>Initializes a parser which can defer named function resolution to an engine.</summary>
    /// <param name="allowUnregisteredFunctions">Defers unknown function binding to an engine catalog; the default keeps standalone unknown-name diagnostics.</param>
    /// <param name="options">Optional shared analyzer configuration.</param>
    public OqlQueryParser(bool allowUnregisteredFunctions, QueryParserOptions? options = null) : base(options ?? new QueryParserOptions())
    {
        _allowUnregisteredFunctions = allowUnregisteredFunctions;
    }

    /// <inheritdoc />
    protected override QueryLanguageProfile Profile => OqlLanguageProfile.Instance;

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
        _position = 0;
        _depth = 0;
        int line = 1;
        int scanned = 0;
        while (lexer.MoveNext())
        {
            var token = lexer.Current;
            // Lines break where the lexer ends a line comment (#1150). Counting each gap on its
            // own is exact: a token never starts with LF, so no CR LF straddles two gaps.
            line += TokenLexer.CountLineBreaks(_source.AsSpan(scanned, token.Position - scanned));
            scanned = token.Position;

            var item = new Lexeme(token.Type, token.Value.ToString(), token.Position,
                token.Position + token.Value.Length, line);
            if (token.Type == TokenType.Comment)
            {
                if (!IsCompleteComment(item.Text))
                {
                    Error("CD00003", ErrorMessages.OqlQueryParserParseCoreVoid, item);
                }
            }
            else
            {
                _tokens.Add(item);
                if (token.Type is TokenType.String or TokenType.QuotedIdentifier && !IsCompleteQuoted(item.Text))
                {
                    Error("CD00003", ErrorMessages.OqlQueryParserParseCoreVoid2, item);
                }
                else if (token.Type == TokenType.Unrecognized && token.Value[0] != '?')
                {
                    // A character OQL does not use is never a name or an expression, so the
                    // statement is not parsed and nothing is bound (#1101).
                    Error("CD00002", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.OqlQueryParserParseCoreVoid3, DescribeCharacter(item.Text)), item);
                }
            }
        }
        line += TokenLexer.CountLineBreaks(_source.AsSpan(scanned));

        _tokens.Add(new Lexeme(TokenType.Eof, string.Empty, _source.Length, _source.Length, line));
        FindComprehensionTails();

        // Capability checks precede syntax parsing, so a known unsupported construct
        // cannot be disguised by the first downstream syntax error it would cause.
        var unsupported = FindUnsupported();
        OqlExpression expression;
        if (unsupported.Count != 0 || _diagnostics.Count != 0)
        {
            expression = EmptyExpression();
        }
        else if (Current.Type == TokenType.Eof)
        {
            Error("CD00001", ErrorMessages.OqlQueryParserParseCoreVoid4, Current);
            expression = EmptyExpression();
        }
        else if (Is("EXPLAIN"))
        {
            var start = Current;
            _position++;
            if (Is("SELECT"))
            {
                expression = new OqlExplainExpression(ParseSelect(), Span(start, _tokens[_position - 1]));
            }
            else if (Is("INSERT"))
            {
                expression = new OqlExplainExpression(ParseWrite(), Span(start, Previous));
            }
            else if (Is("UPSERT"))
            {
                expression = new OqlExplainExpression(ParseWrite(upsert: true), Span(start, Previous));
            }
            else if (Is("DELETE"))
            {
                expression = new OqlExplainExpression(ParseDelete(), Span(start, Previous));
            }
            else if (Is("UPDATE"))
            {
                expression = new OqlExplainExpression(ParseUpdate(), Span(start, Previous));
            }
            else
            {
                _diagnostics.Add(QueryDiagnostics.UnsupportedClause($"EXPLAIN {Current.Text}", Profile.Language, Span(start, Current)));
                expression = EmptyExpression();
            }
        }
        else if (Is("BEGIN") || Is("START") || Is("COMMIT") || Is("ROLLBACK"))
        {
            expression = ParseTransaction();
        }
        else if (Is("SELECT"))
        {
            expression = ParseSelect();
        }
        else if (Is("INSERT"))
        {
            expression = ParseWrite();
        }
        else if (Is("UPSERT"))
        {
            expression = ParseWrite(upsert: true);
        }
        else if (Is("DELETE"))
        {
            expression = ParseDelete();
        }
        else if (Is("UPDATE"))
        {
            expression = ParseUpdate();
        }
        else if (IsIndexStatement("CREATE"))
        {
            expression = ParseCreateIndex();
        }
        else if (IsIndexStatement("DROP"))
        {
            expression = ParseDropIndex();
        }
        else if (IsDdlStatement("CREATE", "COLLECTION"))
        {
            expression = ParseCreateCollection();
        }
        else if (IsDdlStatement("DROP", "COLLECTION"))
        {
            expression = ParseDropCollection();
        }
        else
        {
            Error("CD00002", ErrorMessages.OqlQueryParserParseCoreVoid5, Current);
            expression = EmptyExpression();
        }

        if (unsupported.Count == 0 && _diagnostics.Count == 0)
        {
            if (Is("RETURNING"))
            {
                _diagnostics.Add(QueryDiagnostics.UnsupportedClause("RETURNING", Profile.Language, Span(Current, Current)));
            }
            else if (Is("LIMIT") || Is("OFFSET"))
            {
                _diagnostics.Add(QueryDiagnostics.UnsupportedClause(Current.Text.ToUpperInvariant(), Profile.Language, Span(Current, Current)));
            }
            else if (Take(TokenType.Semicolon) && Current.Type != TokenType.Eof)
            {
                Error("CD00002", ErrorMessages.OqlQueryParserParseCoreVoid6, Current);
            }
            else if (Current.Type != TokenType.Eof)
            {
                Error("CD00002", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.OqlQueryParserParseCoreVoid7, Current.Text), Current);
            }
        }

        // '?' still binds nothing. Only an operand receives the parameter-form message;
        // names, paths and trailing positions keep lexical strictness's generic message.
        foreach (var token in _tokens)
        {
            if (token.Type != TokenType.Unrecognized || token.Text != "?") { continue; }
            if (!_parameterFormPositions.Contains(token.Start))
            {
                _diagnostics.RemoveAll(diagnostic => diagnostic.Start == token.Start);
                Error("CD00002", ErrorMessages.OqlQueryParserParseCoreVoid8, token);
            }
            expression = EmptyExpression();
        }
        expression.SetStatementText(_source);
        var statement = new OqlQueryStatement(expression);
        foreach (var (clause, token) in unsupported)
        {
            if (clause == OqlClauses.Returning)
            { statement.AddDiagnostic(QueryDiagnostics.UnsupportedClause(clause, Profile.Language, Span(token, token))); }
            else { RequireClause(statement, clause, Span(token, token)); }
        }

        foreach (var diagnostic in _diagnostics)
        {
            statement.AddDiagnostic(diagnostic);
        }

        return statement;
    }

    /// <summary>
    /// Finds every recognized construct outside the executable profile, using the
    /// recognized-unsupported table (<see cref="OqlUnsupportedVocabulary"/>). Each construct
    /// is reported once, so a statement whose first word is not OQL reports that word only,
    /// and a quantified predicate reports its quantifier rather than its <c>IN</c>.
    /// </summary>
    private List<(string Clause, Lexeme Token)> FindUnsupported()
    {
        List<(string, Lexeme)> result = [];
        int statementStart = IsWord(_tokens[0], "EXPLAIN") ? 1 : 0;
        var (updateHeaderEnd, updateClause) = FindUpdateClause(statementStart);
        bool hasGroupBy = false;
        int parentheses = 0;
        bool inSelectList = false;
        int selectHead = -2;
        Stack<(int Depth, bool InSelectList, int SelectHead, bool HasGroupBy)>? selectScopes = null;
        int callQuantifier = -2;
        int fetchHead = -2;
        int pagingHead = -2;
        int openQuantifiers = 0;
        var expressionScopes = new Stack<bool>();
        HashSet<int>? existsBindings = null;
        var (indexKeyStart, indexKeyEnd) = FindIndexKeyRange();
        for (int index = 0; index < _tokens.Count; index++)
        {
            var token = _tokens[index];
            if (token.Type == TokenType.Semicolon)
            {
                break;
            }

            if (token.Type == TokenType.LeftParen) { parentheses++; }
            else if (token.Type == TokenType.RightParen)
            {
                parentheses--;
                if (selectScopes is { Count: > 0 } && selectScopes.Peek().Depth > parentheses)
                {
                    var enclosing = selectScopes.Pop();
                    inSelectList = enclosing.InSelectList;
                    selectHead = enclosing.SelectHead;
                    hasGroupBy = enclosing.HasGroupBy;
                }
            }

            if (token.Type is not (TokenType.Keyword or TokenType.Function or TokenType.Identifier))
            {
                continue;
            }

            // A keyword-shaped name in adjacent :name is part of the rejected operand,
            // not a clause, subquery or unsupported operation. Syntax parsing owns it.
            if (index > 0 && (_tokens[index - 1].Type == TokenType.Dot ||
                _tokens[index - 1].Type == TokenType.Colon && _tokens[index - 1].End == token.Start &&
                !IsObjectMemberValue(index)))
            {
                continue;
            }

            string value = token.Text.ToUpperInvariant();
            // UPDATE's parsed destination and alias keep ordinary contextual names such
            // as use. SET is exempt only at the assignment-clause token after that header;
            // SET(...) elsewhere still refuses.
            if (index > statementStart && (index < updateHeaderEnd || index == updateClause)) { continue; }
            // An unsupported source expansion owns the remainder of UPDATE. Syntax parsing
            // locates it after the destination, rather than guessing from expression names.
            if (index == updateHeaderEnd && updateClause < 0 && (IsUnnestClause(index) || IsJoinClause(index))) { break; }

            // The ORDER BY parser owns NULLS options, including malformed quantifiers.
            if (value is "FIRST" or "LAST" or "ALL" or "DISTINCT" && index > 0 && IsWord(_tokens[index - 1], "NULLS")) { continue; }

            // FETCH FIRST is one unsupported spelling. LIMIT ALL is an invalid count.
            // Only a recognized clause head owns the next word; a path or alias does not.
            if (value == "FIRST" && index == fetchHead + 1 || value == "ALL" && index == pagingHead + 1) { continue; }

            // The DDL parser owns array-key quantifiers and the comprehension binding words.
            if (value is ("ALL" or "DISTINCT" or "ARRAY" or "FOR" or "IN" or "END") && indexKeyStart >= 0 && index > indexKeyStart && index < indexKeyEnd) { continue; }
            if (value == "UNIQUE" && index == 1 && IsIndexStatement("CREATE")) { continue; }

            // DISTINCT in an IS [NOT] DISTINCT FROM predicate is an operator suffix.
            if (value == "DISTINCT" && index > 0 && (IsWord(_tokens[index - 1], "IS") ||
                index > 1 && IsWord(_tokens[index - 1], "NOT") && IsWord(_tokens[index - 2], "IS"))) { continue; }
            if (IsDistinctFromToken(index)) { continue; }

            // SELECT and call-head quantifiers are parsed by their respective grammar rules.
            if (openQuantifiers == 0 && value is "DISTINCT" or "ALL")
            {
                if (index == selectHead + 1) { continue; }
                if (index == callQuantifier + 1 || index > 1 && _tokens[index - 1].Type == TokenType.LeftParen &&
                    _tokens[index - 2].Type is TokenType.Function or TokenType.Identifier or TokenType.QuotedIdentifier &&
                    (!OqlUnsupportedVocabulary.TryFind(_tokens[index - 2].Text.ToUpperInvariant(), out var deferred) ||
                        deferred.Position != OqlWordPosition.Function))
                { callQuantifier = index; continue; }
                bool function = index > 1 && _tokens[index - 1].Type == TokenType.LeftParen &&
                    (_tokens[index - 2].Type == TokenType.Function ||
                        OqlUnsupportedVocabulary.TryFind(_tokens[index - 2].Text.ToUpperInvariant(), out var unsupportedFunction) &&
                        unsupportedFunction.Position == OqlWordPosition.Function);
                string construct = function
                    ? _tokens[index - 2].Text.ToUpperInvariant() + "(" + value + ")" : value;
                // A deferred function was already recognized at its name. Its quantifier
                // refines that one construct rather than adding a second refusal.
                if (function && result.Count > 0 && result[^1].Item2.Start == _tokens[index - 2].Start)
                { result.RemoveAt(result.Count - 1); }
                _diagnostics.Add(QueryDiagnostics.UnsupportedClause(construct, Profile.Language, Span(token, token)));
                break;
            }
            // ColLabel accepts reserved words after AS; quantifier pins remain positional.
            if (inSelectList && index > 0 && IsWord(_tokens[index - 1], "AS")) { continue; }
            if (value == "FROM") { inSelectList = false; }
            if (value == "RETURNING" && index > statementStart && parentheses == 0 && EndsOperand(index - 1) &&
                !IsWord(_tokens[index - 1], "INTO") && !IsWord(_tokens[index - 1], "FROM") && !IsWord(_tokens[index - 1], "AS"))
            {
                inSelectList = true;
            }

            // A SQL++ quantified predicate, EVERY x IN ... SATISFIES ... END, is one construct:
            // its binding IN is not the IN predicate, and its text up to END belongs to it.
            bool quantifier = value is "EVERY" or "ANY" or "SOME" && IsQuantifierBinding(index + 1);
            if (openQuantifiers > 0)
            {
                if (quantifier)
                {
                    openQuantifiers++;
                    index += 2;
                }
                else if (value == "CASE" || value is "ARRAY" or "FIRST" && IsComprehension(index))
                {
                    // CASE shares END with quantified predicates. Its END must not expose
                    // the rest of the unsupported predicate to independent capability scans.
                    openQuantifiers++;
                }
                else if (value == "END")
                {
                    openQuantifiers--;
                }
                continue;
            }
            if (value is "ARRAY" or "FIRST" && IsComprehension(index) &&
                !(index > 0 && EndsOperand(index - 1) && IsWord(_tokens[index + 1], "FROM")))
            {
                string clause = value == "ARRAY" ? OqlClauses.ArrayComprehension : OqlClauses.FirstComprehension;
                if (!Supports(clause)) { AddUnsupported(result, clause, token); openQuantifiers++; }
                else { expressionScopes.Push(true); }
                continue;
            }
            if (value == "CASE" && expressionScopes.Count > 0) { expressionScopes.Push(false); }
            if (value == "END" && expressionScopes.Count > 0) { expressionScopes.Pop(); }
            if (value == "FOR" && expressionScopes.Count > 0 && expressionScopes.Peek()) { continue; }
            if (quantifier)
            {
                if (expressionScopes.Count > 0) { expressionScopes.Push(false); }
                string clause = value == "EVERY" ? OqlClauses.Every : OqlClauses.Any;
                if (!Supports(clause))
                {
                    AddUnsupported(result, clause, token);
                    openQuantifiers++;
                }
                index += 2;
                continue;
            }

            if (value == "ANY" && index + 2 < _tokens.Count && IsWord(_tokens[index + 1], "AND") && IsWord(_tokens[index + 2], "EVERY"))
            {
                AddUnsupported(result, "ANY AND EVERY", token);
                openQuantifiers++;
                index += 2;
                continue;
            }
            // The binding itself is not array membership. WITHIN remains deferred.
            if (value is "ANY" or "SOME" or "EVERY" && index + 2 < _tokens.Count && IsWord(_tokens[index + 2], "WITHIN"))
            { continue; }
            if (value == "MERGE" && index > 0 && index + 1 < _tokens.Count && IsWord(_tokens[index + 1], "INTO") &&
                (IsWord(_tokens[index - 1], "SATISFIES") || IsWord(_tokens[index - 1], "THEN") ||
                    IsWord(_tokens[index - 1], "ELSE") || _tokens[index - 1].Type == TokenType.LeftParen))
            {
                // A nested MERGE statement owns its text, while a field named merge
                // remains an ordinary expression in SATISFIES.
                AddUnsupported(result, "MERGE", token);
                break;
            }

            // EXISTS is an expression operator and a positional DDL modifier. Keep malformed
            // modifiers on the syntax path too (CREATE COLLECTION IF EXISTS reports NOT).
            if (value == "EXISTS" && (IsDdlExistenceToken(index) ||
                !IsWord(_tokens[statementStart], "CREATE") && !IsWord(_tokens[statementStart], "DROP") &&
                index > statementStart && !IsExistsBindingQuantifier(index, ref existsBindings)))
            {
                continue;
            }

            if (value is "CREATE" or "DROP" && index + 1 < _tokens.Count &&
                (IsWord(_tokens[index + 1], "INDEX") || IsWord(_tokens[index + 1], "COLLECTION") ||
                    value == "CREATE" && index + 2 < _tokens.Count && IsWord(_tokens[index + 1], "UNIQUE") && IsWord(_tokens[index + 2], "INDEX")))
            {
                bool collection = IsWord(_tokens[index + 1], "COLLECTION");
                string compound = (value, collection) switch
                {
                    ("CREATE", true) => OqlClauses.CreateCollection,
                    ("DROP", true) => OqlClauses.DropCollection,
                    ("CREATE", false) => OqlClauses.CreateIndex,
                    _ => OqlClauses.DropIndex,
                };
                if (!Supports(compound))
                {
                    var end = _tokens[index + 1];
                    result.Add((compound, new Lexeme(token.Type, compound, token.Start, end.End, token.Line)));
                }
                if (!IsWord(_tokens[index + 1], "UNIQUE")) { index++; }
                continue;
            }
            if (value == "SELECT")
            {
                if (parentheses > 0)
                { (selectScopes ??= new()).Push((parentheses, inSelectList, selectHead, hasGroupBy)); }
                selectHead = index;
                inSelectList = true;
                hasGroupBy = false;
                continue;
            }

            if (value is "LIMIT" or "OFFSET" && !inSelectList && index > 0 && EndsOperand(index - 1))
            {
                pagingHead = index;
                continue;
            }

            if (value == "GROUP" && index + 1 < _tokens.Count && IsWord(_tokens[index + 1], "AS"))
            {
                AddUnsupported(result, OqlClauses.GroupAs, token);
                continue;
            }
            if (value == "GROUP" && index + 1 < _tokens.Count && IsWord(_tokens[index + 1], "BY")) { hasGroupBy = true; }
            // LETTING is a deferred grouping clause, but remains an ordinary field or
            // FROM/UNNEST binding name before that clause, including implicit aliases.
            if (value == "LETTING" && index > statementStart && !hasGroupBy) { continue; }

            // ODMG's quantifiers, FOR ALL x IN e: p and EXISTS x IN e: p, bind with IN too.
            if (value == "FOR" && index + 1 < _tokens.Count && IsWord(_tokens[index + 1], "ALL") &&
                IsQuantifierBinding(index + 2))
            {
                AddUnsupported(result, "FOR ALL", token);
                index += 3;
                continue;
            }
            if (value == "EXISTS" && IsExistsBindingQuantifier(index, ref existsBindings))
            {
                _diagnostics.Add(QueryDiagnostics.UnsupportedClause("EXISTS binding quantifier", Profile.Language, Span(token, token)));
                index += 2;
                continue;
            }

            if (value == "FOR" && statementStart < _tokens.Count && IsWord(_tokens[statementStart], "UPDATE"))
            {
                AddUnsupported(result, "FOR", token);
                break;
            }
            if (statementStart < _tokens.Count && IsWord(_tokens[statementStart], "UPDATE"))
            {
                if (value == "USE" && EndsOperand(index - 1))
                {
                    AddUnsupported(result, "USE", token);
                    break;
                }
                // Keep the UPDATE grammar responsible for its own LIMIT refusal.
                if (value == "LIMIT") { continue; }
            }

            if (value == "EXISTS")
            {
                _diagnostics.Add(QueryDiagnostics.UnsupportedClause("EXISTS", Profile.Language, Span(token, token)));
                break;
            }
            if (value is "JOIN" or "NEST" && selectHead >= statementStart) { continue; }
            if (!OqlUnsupportedVocabulary.TryFind(value, out var word) || Supports(word.Construct) && value is not ("RETURNING" or "JOIN" or "NEST"))
            {
                continue;
            }

            // Words of other languages are not reserved field names: they name a construct
            // only where it can start, so mixed-shape fields such as fetch still parse. A
            // statement verb names one only as the first word, so an AS-less FROM alias named
            // merge or upsert still parses, and a quantifier word only through the binding
            // path above, so an alias named every or satisfies does too.
            bool constructPosition = word.Position switch
            {
                OqlWordPosition.Anywhere => true,
                OqlWordPosition.Clause => index == 0 || EndsOperand(index - 1),
                OqlWordPosition.Statement => index == 0,
                OqlWordPosition.Trailing => index == 0,
                OqlWordPosition.Function => index + 1 < _tokens.Count && _tokens[index + 1].Type == TokenType.LeftParen,
                _ => false,
            };
            if (!constructPosition)
            {
                continue;
            }

            if (value is "JOIN" or "NEST")
            { _diagnostics.Add(QueryDiagnostics.UnsupportedClause(value + " in UPDATE or DELETE", Profile.Language, Span(token, token))); break; }

            // UNION ALL and UNION DISTINCT are one set operation, not a set operation plus the
            // ALL or DISTINCT word.
            var reported = token;
            if (value is "UNION" or "INTERSECT" or "EXCEPT" && index + 1 < _tokens.Count &&
                (IsWord(_tokens[index + 1], "ALL") || IsWord(_tokens[index + 1], "DISTINCT")))
            {
                var modifier = _tokens[++index];
                reported = new Lexeme(token.Type, token.Text, token.Start, modifier.End, token.Line);
            }

            result.Add((word.Construct, reported));
            if (value == "FETCH") { fetchHead = index; }

            // A statement that does not start as OQL is one unsupported construct; the rest of
            // its text belongs to it (MERGE INTO c USING ...).
            if (index == 0)
            {
                break;
            }

        }
        return result;
    }

    private (int HeaderEnd, int Clause) FindUpdateClause(int start)
    {
        if (start >= _tokens.Count || !IsWord(_tokens[start], "UPDATE")) { return (-1, -1); }
        int saved = _position;
        int diagnostics = _diagnostics.Count;
        _position = start + 1;
        CollectionName();
        if (Take("AS")) { Identifier(); }
        else if (IsUpdateAlias()) { Identifier(); }
        var result = (_position, Is("SET") || Is("UNSET") ? _position : -1);
        _position = saved;
        _diagnostics.RemoveRange(diagnostics, _diagnostics.Count - diagnostics);
        return result;
    }

    private void AddUnsupported(List<(string, Lexeme)> result, string construct, Lexeme token)
    {
        if (!Supports(construct))
        {
            result.Add((construct, token));
        }
    }

    private bool IsComprehension(int index)
    {
        // Parenthesized mappings are legal; the retired ARRAY()/FIRST() calls stay refused.
        if (index + 1 >= _tokens.Count) { return false; }
        return _tokens[index + 1].Type != TokenType.LeftParen || _comprehensionTails[index + 1];
    }

    private void FindComprehensionTails()
    {
        // Memoize balanced lookahead in linear work, including inputs much deeper than
        // the parser's limit. Re-scanning each nested parenthesized mapping is quadratic.
        _comprehensionTails = new bool[_tokens.Count];
        var delimiters = new Stack<int>();
        for (int index = _tokens.Count - 1; index >= 0; index--)
        {
            var token = _tokens[index];
            int next = index + 1;
            // Reserved words can still name dotted members, AS labels and object keys.
            bool expressionWord = (index == 0 || _tokens[index - 1].Type != TokenType.Dot && !IsWord(_tokens[index - 1], "AS")) &&
                (next >= _tokens.Count || _tokens[next].Type != TokenType.Colon);
            if (token.Type is TokenType.RightParen or TokenType.RightBracket or TokenType.RightBrace ||
                expressionWord && IsWord(token, "END")) { delimiters.Push(index); }
            else if (token.Type is TokenType.LeftParen or TokenType.LeftBracket or TokenType.LeftBrace)
            {
                TokenType close = token.Type switch
                {
                    TokenType.LeftParen => TokenType.RightParen,
                    TokenType.LeftBracket => TokenType.RightBracket,
                    _ => TokenType.RightBrace,
                };
                next = delimiters.TryPeek(out int end) && _tokens[end].Type == close ? delimiters.Pop() + 1 : _tokens.Count;
            }
            else if (expressionWord && (IsWord(token, "CASE") ||
                (IsWord(token, "ARRAY") || IsWord(token, "FIRST")) && IsComprehension(index) ||
                (IsWord(token, "ANY") || IsWord(token, "SOME") || IsWord(token, "EVERY")) && IsQuantifierBinding(next)))
            {
                // Skip the entire nested expression, including its FOR/WHEN, before looking
                // for this comprehension's binding. The reverse pass already classified it.
                next = delimiters.TryPeek(out int end) && IsWord(_tokens[end], "END") ? delimiters.Pop() + 1 : _tokens.Count;
            }
            bool distinctFrom = IsDistinctFromToken(index);
            bool stop = token.Type is TokenType.Comma or TokenType.Semicolon or TokenType.Eof or TokenType.RightParen ||
                expressionWord && (IsWord(token, "FROM") && !distinctFrom || IsWord(token, "AS") || IsWord(token, "END"));
            _comprehensionTails[index] = !stop && (IsWord(token, "FOR") || next < _tokens.Count && _comprehensionTails[next]);
        }
    }

    private bool IsDistinctFromToken(int index) => index > 1 && IsWord(_tokens[index], "FROM") &&
        IsWord(_tokens[index - 1], "DISTINCT") && (IsWord(_tokens[index - 2], "IS") ||
        index > 2 && IsWord(_tokens[index - 2], "NOT") && IsWord(_tokens[index - 3], "IS"));

    private bool IsExistsBindingQuantifier(int index, ref HashSet<int>? bindings)
    {
        if (!IsQuantifierBinding(index + 1)) { return false; }
        bindings ??= FindExistsBindingQuantifiers();
        return bindings.Contains(index);
    }

    private HashSet<int> FindExistsBindingQuantifiers()
    {
        // Classify colon-bearing ODMG bindings once, in linear work, before the
        // recursive parser applies its depth limit. Object/parameter scopes inside
        // balanced delimiters do not turn unary EXISTS followed by IN into a binding.
        var result = new HashSet<int>();
        var scopes = new Stack<bool>();
        bool colon = false;
        for (int index = _tokens.Count - 1; index >= 0; index--)
        {
            var token = _tokens[index];
            if (token.Type is TokenType.RightParen or TokenType.RightBracket or TokenType.RightBrace)
            { scopes.Push(colon); colon = false; }
            else if (token.Type is TokenType.LeftParen or TokenType.LeftBracket or TokenType.LeftBrace)
            { colon = scopes.TryPop(out bool enclosing) && enclosing; }
            else if (token.Type == TokenType.Colon) { colon = true; }
            else if (token.Type is TokenType.Comma or TokenType.Semicolon or TokenType.Eof ||
                IsWord(token, "FROM") && !IsDistinctFromToken(index) || IsWord(token, "AND") || IsWord(token, "OR")) { colon = false; }
            else if (colon && IsWord(token, "EXISTS") && IsQuantifierBinding(index + 1)) { result.Add(index); }
        }
        return result;
    }

    /// <summary>
    /// Whether the tokens at <paramref name="index"/> bind a quantifier variable:
    /// <c>name IN</c>, as in <c>EVERY x IN c.items</c> or ODMG's <c>FOR ALL x IN c.items</c>.
    /// </summary>
    private bool IsQuantifierBinding(int index) => index + 1 < _tokens.Count &&
        _tokens[index].Type is TokenType.Identifier or TokenType.QuotedIdentifier or TokenType.Function &&
        IsWord(_tokens[index + 1], "IN");

    /// <summary>
    /// Whether the token at <paramref name="index"/> ends an operand, so that the word after
    /// it starts a clause: a name, a path segment, a literal, a parameter, a closing bracket,
    /// or <c>ASC</c>/<c>DESC</c>. <c>WHERE a = 1 LIMIT 5</c> and <c>ORDER BY a DESC OFFSET 2</c>
    /// end an operand; <c>SELECT limit</c>, <c>, merge</c> and <c>AS limit</c> do not.
    /// </summary>
    private bool EndsOperand(int index)
    {
        var token = _tokens[index];
        return token.Type switch
        {
            TokenType.Identifier or TokenType.QuotedIdentifier or TokenType.Function or
            TokenType.RightParen or TokenType.RightBracket or TokenType.RightBrace or TokenType.Parameter or
            TokenType.Integer or TokenType.Float or TokenType.String => true,
            TokenType.Keyword => index > 0 && _tokens[index - 1].Type == TokenType.Dot ||
                token.Text.ToUpperInvariant() is "NULL" or "MISSING" or "NIL" or "TRUE" or "FALSE" or "VALUED" or "END" or "ASC" or "DESC",
            _ => false,
        };
    }

    private static bool IsWord(Lexeme token, string word) =>
        token.Type is TokenType.Keyword or TokenType.Identifier or TokenType.Function &&
        string.Equals(token.Text, word, StringComparison.OrdinalIgnoreCase);

    // In {key:expr}, the colon belongs to the member rather than an adjacent :name
    // parameter. Keys follow an opening brace or a member comma and are literal names.
    private bool IsObjectMemberValue(int index) => index >= 3 &&
        _tokens[index - 3].Type is TokenType.LeftBrace or TokenType.Comma &&
        _tokens[index - 2].Type is TokenType.Identifier or TokenType.QuotedIdentifier or
            TokenType.String or TokenType.Keyword or TokenType.Function;

    private OqlSelectExpression EmptyExpression() => new(string.Empty, null, [], null, [], null, [], false, false, null, null, Span(Current, Current));
    private bool IsIndexStatement(string verb) => IsDdlStatement(verb, "INDEX") || verb == "CREATE" &&
        IsDdlStatement("CREATE", "UNIQUE") && _position + 2 < _tokens.Count && IsWord(_tokens[_position + 2], "INDEX");
    private bool IsDdlStatement(string verb, string kind) => Is(verb) && _position + 1 < _tokens.Count &&
        IsWord(_tokens[_position + 1], kind);
    private Lexeme Current => _tokens[Math.Min(_position, _tokens.Count - 1)];
    private Lexeme Previous => _tokens[Math.Max(0, _position - 1)];
    private bool Failed => _diagnostics.Count != 0;
    private void Advance()
    {
        if (_position < _tokens.Count - 1)
        {
            _position++;
        }
    }
    private bool Is(string text) => Current.Type is not (TokenType.String or TokenType.QuotedIdentifier) &&
        string.Equals(Current.Text, text, StringComparison.OrdinalIgnoreCase);
    private bool Take(string text)
    {
        if (!Is(text))
        {
            return false;
        }
        Advance();
        return true;
    }
    private bool Take(TokenType type)
    {
        if (Current.Type != type)
        {
            return false;
        }
        Advance();
        return true;
    }
    private void Expect(string text)
    {
        if (!Take(text))
        {
            Error("CD00002", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.OqlQueryParserExpectVoid, text), Current);
        }
    }
    private void Expect(TokenType type, string text)
    {
        if (!Take(type))
        {
            Error("CD00002", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.OqlQueryParserExpectVoid, text), Current);
        }
    }
    private static Location Span(Lexeme start, Lexeme end) =>
        Location.Create(start.Line, end.Line + TokenLexer.CountLineBreaks(end.Text), start.Start, end.End);

    private string CollectionName()
    {
        string name = Identifier();
        // This namespace identifies virtual collections in the current database.
        // It does not introduce general database-qualified collection names.
        if (string.Equals(name, "COHESION_SCHEMA", StringComparison.OrdinalIgnoreCase) && Take(TokenType.Dot))
        {
            name += "." + Identifier();
        }
        return name;
    }

    private string Identifier(bool allowKeyword = false)
    {
        var token = Current;
        if (token.Type == TokenType.QuotedIdentifier)
        {
            Advance();
            if (token.Text.Length == 2)
            {
                Error("CD00002", ErrorMessages.OqlQueryParserIdentifierVoid, token);
            }

            return token.Text.Length >= 2 ? token.Text[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal) : string.Empty;
        }
        if ((token.Type is TokenType.Identifier or TokenType.Function || allowKeyword && token.Type == TokenType.Keyword) &&
            token.Text.Length > 0 && (char.IsLetter(token.Text[0]) || token.Text[0] == '_'))
        {
            Advance();
            return token.Text;
        }
        Error("CD00002", ErrorMessages.OqlQueryParserIdentifierVoid2, token);
        return string.Empty;
    }

    private void Error(string code, string message, Lexeme token)
    {
        // Recovery can reach one bad token from several rules, as with SELECT ~a FROM c. It is
        // one mistake, so a second error with the same code at the same span is dropped (#1101).
        foreach (var existing in _diagnostics)
        {
            if (existing.Code == code && existing.Start == token.Start && existing.End == token.End)
            {
                return;
            }
        }

        _diagnostics.Add(new Diagnostic
        {
            Code = code, Message = message, Start = token.Start, End = token.End,
            Line = token.Line, Severity = DiagnosticSeverity.Error, Location = DiagnosticLocation.Absolute,
        });
    }

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
        if (text.Length < 2)
        {
            return false;
        }

        char quote = text[0];
        for (int i = 1; i < text.Length; i++)
        {
            if (text[i] != quote)
            {
                continue;
            }

            if (i + 1 < text.Length && text[i + 1] == quote) { i++; continue; }
            return i == text.Length - 1;
        }
        return false;
    }

    private static bool IsCompleteComment(string text)
    {
        if (!text.StartsWith("/*", StringComparison.Ordinal))
        {
            return true;
        }

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
        public DebugView(global::Assimalign.Cohesion.Database.Documents.Language.OqlQueryParser value)
        {
            _ = value;
        }
    }
}
