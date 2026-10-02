using System.Runtime.CompilerServices;

namespace Assimalign.Cohesion.Database.Sql.Language;

using Assimalign.Cohesion.Database.Language;

public sealed partial class SqlQueryParser
{
    // ── Expression nesting limit (#1151) ────────────────────────────────
    //
    // Every rule that parses an operand recurses, and so does every walker that later visits
    // the tree (the planner, the evaluator, the renderer). Without a bound, a statement such as
    // 1 + 1 + ... with 200,000 terms, or 200,000 nested parentheses, overflowed the stack, and
    // .NET cannot catch a StackOverflowException: one statement ended the process, and over
    // the wire took every session of the server with it. The dialect therefore bounds two
    // things, each at the configured limit (SqlQueryParserOptions.ExpressionNestingLimit,
    // 256 by default, 32..4096):
    //
    //   - The depth of the expression tree (SqlExpression.Depth), a function of the tree alone.
    //     Every node is a level: an operator, a predicate, CASE, CAST, COLLATE, a function call,
    //     an IN list, a subquery, and a leaf. A chain of AND (or of OR) terms is one node however
    //     many terms it has (SqlLogicalExpression), as in PostgreSQL, so a 10,000-term predicate
    //     is three levels deep. Every other left-associative chain counts each link, so
    //     1 + 1 + ... with N terms is N levels deep. A subquery's clauses count below the node
    //     that holds it, so the limit spans the whole statement.
    //   - The nesting of grouping parentheses. They are not nodes, so they add nothing to the
    //     tree, but each pair is one more level of recursion here.
    //
    // Counting parentheses apart from the tree is what keeps persisted definitions openable.
    // SqlExpressionRenderer adds parentheses only around a node, at most one pair per node, so
    // the canonical text of an accepted tree nests its parentheses no deeper than the tree, and
    // the tree is unchanged: the text the engine stores always parses back under these limits.
    //
    // Depth is checked top-down before the parser recurses into an operand its node will
    // enclose (TryEnterOperand: _expressionDepth counts those enclosing nodes), and bottom-up
    // whenever a node is built over an operand parsed before the node existed, as each link of a
    // left-associative chain is (Nest, and TryOpenChain for an AND/OR chain, which checks its
    // first operand before the rest of the chain is parsed). The two together reject a statement
    // exactly when its tree is deeper than the limit, and they bound the parser's own recursion:
    // every cycle through the rules passes TryEnterOperand or a parenthesis. Crossing either
    // limit reports SQL0006, abandons the rest of the statement and keeps only its command type,
    // the way a character outside the dialect does: nothing past the limit is parsed, so no
    // deeper tree is ever built and the statement never executes. Each entry also checks the
    // thread's stack, and a thread too small for text within the limits gets SQL0007 the same
    // way. The deepest nesting the statement reaches is recorded on its root
    // (SqlQueryStatement.ExpressionNestingDepth), so an engine with a lower limit can refuse a
    // statement another parser accepted without parsing it again.

    /// <summary>The diagnostic for text nested deeper than the parser's limit.</summary>
    internal const string NestingCode = "SQL0006";

    /// <summary>
    /// The diagnostic for text within the limits that the calling thread has too little stack
    /// left to parse. The same text parses on a thread with more stack.
    /// </summary>
    internal const string NestingStackCode = "SQL0007";

    // How deep the expression tree may be, and how deep grouping parentheses may nest.
    private readonly int _nestingLimit;

    // The nodes that will enclose the operand being parsed, across subqueries.
    private int _expressionDepth;

    // The grouping parentheses enclosing the current position.
    private int _parenthesisDepth;

    // The deepest the statement has nested so far: the greater of its deepest expression tree
    // and its deepest grouping parentheses.
    private int _deepestNesting;

    // The index of the SQL0006 or SQL0007 diagnostic in _parseDiagnostics, or -1.
    private int _nestingDiagnostic = -1;

    /// <summary>
    /// Gets the expression nesting limit this parser applies: how deep an expression tree may be,
    /// and how deep grouping parentheses may nest.
    /// </summary>
    internal int ExpressionNestingLimit => _nestingLimit;

    /// <summary>Whether this statement crossed a nesting limit and its remaining text was abandoned.</summary>
    private bool NestingExceeded => _nestingDiagnostic >= 0;

    /// <summary>The parser rule an operand is parsed with.</summary>
    private enum OperandRule
    {
        Expression,
        And,
        Not,
        Addition,
        Multiplication,
        Unary,
        Collate,
    }

    /// <summary>
    /// Parses an operand that the node being built will enclose: one level deeper than the
    /// node's own position. When that level would exceed the limit it reports <c>SQL0006</c>
    /// instead (<c>SQL0007</c> when the thread is out of stack), abandons the statement and
    /// returns a placeholder.
    /// </summary>
    private SqlExpression ParseOperand(ref TokenLexer lexer, OperandRule rule)
    {
        int position = lexer.Current.Position;
        if (!TryEnterOperand(ref lexer))
        {
            return NestingPlaceholder(position);
        }

        var operand = rule switch
        {
            OperandRule.Expression => ParseExpression(ref lexer),
            OperandRule.And => ParseAnd(ref lexer),
            OperandRule.Not => ParseNot(ref lexer),
            OperandRule.Addition => ParseAddition(ref lexer),
            OperandRule.Multiplication => ParseMultiplication(ref lexer),
            OperandRule.Unary => ParseUnary(ref lexer),
            _ => ParseCollate(ref lexer),
        };
        _expressionDepth--;
        return operand;
    }

    /// <summary>
    /// Enters one more enclosing node before an operand is parsed. The node is one level and
    /// its operand at least one more, so the statement is certainly too deep when that sum
    /// exceeds the limit. On failure the statement is abandoned and the depth is unchanged.
    /// </summary>
    private bool TryEnterOperand(ref TokenLexer lexer)
    {
        if (NestingExceeded)
        {
            return false;
        }

        if (_expressionDepth + 2 > _nestingLimit)
        {
            RejectNesting(ref lexer, lexer.Current.Position, CurrentTokenEnd(ref lexer), ExpressionTooDeepMessage);
            return false;
        }

        if (!HasStackToNest(ref lexer, lexer.Current.Position, CurrentTokenEnd(ref lexer)))
        {
            return false;
        }

        _expressionDepth++;
        return true;
    }

    /// <summary>
    /// Enters one more grouping parenthesis, after its <c>(</c>. On failure the statement is
    /// abandoned and the depth is unchanged.
    /// </summary>
    /// <param name="lexer">The lexer, at the token after the <c>(</c>.</param>
    /// <param name="position">Where the <c>(</c> is, which a rejection reports.</param>
    private bool TryEnterParenthesis(ref TokenLexer lexer, int position)
    {
        if (NestingExceeded)
        {
            return false;
        }

        if (_parenthesisDepth >= _nestingLimit)
        {
            RejectNesting(ref lexer, position, position + 1,
                $"Parentheses nest deeper than the supported limit of {_nestingLimit} levels.");
            return false;
        }

        if (!HasStackToNest(ref lexer, position, position + 1))
        {
            return false;
        }

        _parenthesisDepth++;
        Deepen(_parenthesisDepth);
        return true;
    }

    /// <summary>
    /// Checks, at the first operator of an <c>AND</c> or <c>OR</c> chain, the chain node against
    /// the nodes that enclose it. The node encloses its first operand, which was parsed before the
    /// node existed, so the node is one level deeper than that operand, or than the operands of a
    /// parenthesized chain of the same operator that it absorbs. The terms after the operator
    /// are bounded by the checks they pass on the way down, so a chain too deep is rejected here,
    /// before the rest of it is parsed. On failure the statement is abandoned.
    /// </summary>
    /// <param name="lexer">The lexer, at the chain's first operator.</param>
    /// <param name="firstOperandDepth">The depth of the chain's first operand, or of the deepest operand it absorbs.</param>
    private bool TryOpenChain(ref TokenLexer lexer, int firstOperandDepth)
    {
        if (NestingExceeded)
        {
            return false;
        }

        if (_expressionDepth + 1 + firstOperandDepth > _nestingLimit)
        {
            RejectNesting(ref lexer, lexer.Current.Position, CurrentTokenEnd(ref lexer), ExpressionTooDeepMessage);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Checks the depth of a node built over an operand parsed before the node existed, such as
    /// each link of <c>1 + 1 + ...</c>, against the nodes that enclose it. Every other node is
    /// bounded by the checks its operands passed on the way down.
    /// </summary>
    /// <param name="lexer">The lexer, after the node's last token.</param>
    /// <param name="node">The node just built.</param>
    /// <returns><paramref name="node"/>.</returns>
    private SqlExpression Nest(ref TokenLexer lexer, SqlExpression node)
    {
        if (!NestingExceeded)
        {
            if (_expressionDepth + node.Depth > _nestingLimit)
            {
                int start = node.Location?.Start ?? lexer.Current.Position;
                RejectNesting(ref lexer, start, System.Math.Max(_lastTokenEnd, start), ExpressionTooDeepMessage);
            }
            else
            {
                Deepen(_expressionDepth + node.Depth);
            }
        }

        return node;
    }

    /// <summary>
    /// Records a leaf, or the head of a node whose operands are entered when they are parsed, at
    /// the current position: one level below the nodes that enclose it.
    /// </summary>
    private void EnterLeaf() => Deepen(_expressionDepth + 1);

    /// <summary>Records that the statement nests at least <paramref name="levels"/> deep.</summary>
    private void Deepen(int levels)
    {
        if (levels > _deepestNesting)
        {
            _deepestNesting = levels;
        }
    }

    private string ExpressionTooDeepMessage => $"Expression nesting exceeds the supported limit of {_nestingLimit} levels.";

    /// <summary>
    /// A level of parentheses or calls runs the whole precedence ladder: about 1 to 2 KB of stack
    /// in a release build, depending on how far the JIT has optimized it, and about 6 KB in a
    /// debug build. The deepest statement the default limit accepts, 256 pairs of parentheses
    /// around 255 nested calls, parses on a 1 MB thread in a release build. A thread with less,
    /// such as one created with a small maximum size, a debug build, or a statement within a
    /// higher configured limit, may not recurse as far as the limit allows; the parser then
    /// rejects the statement with <c>SQL0007</c> rather than overflow. The code differs from the
    /// limit's <c>SQL0006</c> because the same text parses on a thread with more stack: the
    /// statement is within the dialect, the thread is not big enough for it.
    /// </summary>
    private bool HasStackToNest(ref TokenLexer lexer, int start, int end)
    {
        if (RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            return true;
        }

        RejectNesting(ref lexer, NestingStackCode, start, end,
            "Expression nesting exceeds the stack available to the parser on this thread.");
        return false;
    }

    /// <summary>
    /// Reports <c>SQL0006</c>, or <c>SQL0007</c> for a thread out of stack, and consumes the rest
    /// of the statement, so every rule still on the stack meets the end of the text and returns
    /// without parsing further. What those rules report on the way out is dropped at the end of
    /// <see cref="ParseCore"/>.
    /// </summary>
    private void RejectNesting(ref TokenLexer lexer, int start, int end, string message)
        => RejectNesting(ref lexer, NestingCode, start, end, message);

    private void RejectNesting(ref TokenLexer lexer, string code, int start, int end, string message)
    {
        _nestingDiagnostic = _parseDiagnostics.Count;
        _parseDiagnostics.Add(new Diagnostic
        {
            Code = code,
            Message = message,
            Start = start,
            End = end,
            Severity = DiagnosticSeverity.Error,
            Location = DiagnosticLocation.Absolute,
        });

        if (!IsAtEnd(ref lexer))
        {
            ConsumeRemaining(ref lexer);
        }
    }

    /// <summary>
    /// Drops the diagnostics the abandoned rules reported after <c>SQL0006</c> or <c>SQL0007</c>:
    /// each met the end of the text the rejection consumed, so none describes the statement as
    /// written.
    /// </summary>
    private void DropDiagnosticsAfterNestingLimit()
    {
        int first = _nestingDiagnostic + 1;
        if (first < _parseDiagnostics.Count)
        {
            _parseDiagnostics.RemoveRange(first, _parseDiagnostics.Count - first);
        }
    }

    private static SqlLiteralExpression NestingPlaceholder(int position)
        => new("NULL", SqlLiteralType.Null, Location.Create(1, 1, position, position));

    private static int CurrentTokenEnd(ref TokenLexer lexer)
        => lexer.Current.Position + (IsAtEnd(ref lexer) ? 0 : lexer.Current.Value.Length);
}
