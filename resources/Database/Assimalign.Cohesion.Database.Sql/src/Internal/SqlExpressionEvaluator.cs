using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// Binds SQL scalar expressions to a row shape and evaluates the bound form against rows. Null
/// propagates SQL-style: any null operand makes a comparison or arithmetic result null, and a null
/// predicate result filters the row out.
/// </summary>
/// <remarks>
/// <para>
/// An instance is a binding scope: the columns of the row an expression is evaluated over (with
/// the join bindings that qualify them), the statement's parameter values, the slots a grouping or
/// an ordering bound whole expressions to, the subquery slots of the statement and the database
/// default collation. <see cref="Bind(SqlExpression)"/> compiles an expression against the scope
/// once (<c>SqlExpressionEvaluator.Binding.cs</c>); <see cref="Evaluate(SqlBoundExpression, object?[])"/>
/// walks the bound tree for each row and resolves nothing by name. The planner binds every
/// expression a plan evaluates, and the executor evaluates them with the statement's materialized
/// subquery values (<see cref="ForExecution"/>).
/// </para>
/// <para>
/// The scope also answers the planner's own questions, which it asks once per statement, never per
/// row: a column's ordinal, type and collation, the collation two expressions compare under, and a
/// parameter's supplied value.
/// </para>
/// </remarks>
internal sealed partial class SqlExpressionEvaluator
{
    /// <summary>The boxed Boolean results every predicate returns, shared rather than boxed per row.</summary>
    private static readonly object True = true;

    /// <summary>The boxed Boolean results every predicate returns, shared rather than boxed per row.</summary>
    private static readonly object False = false;

    /// <summary>Backs <see cref="ForExecution"/> for a statement without subquery values.</summary>
    private static readonly SqlExpressionEvaluator RowEvaluator = new(Array.Empty<SqlCatalogColumn>(), null);

    private readonly IReadOnlyList<SqlCatalogColumn> _columns;
    private readonly IReadOnlyDictionary<string, object?>? _parameters;
    private readonly IReadOnlyList<SqlTableBinding>? _bindings;
    private readonly IReadOnlyDictionary<SqlExpression, int>? _valueOrdinals;
    private readonly Collation _defaultCollation;
    private readonly IReadOnlyDictionary<SqlExpression, SqlProjection>? _projectionSources;

    /// <summary>
    /// The subquery slots the planner lowered this statement's subqueries to, keyed by the node that
    /// spells each one; binding turns such a node into a slot read.
    /// </summary>
    private readonly IReadOnlyDictionary<SqlExpression, SqlSubquerySlot>? _subquerySlots;

    /// <summary>
    /// Values the enclosing subquery plans materialized, by slot. Read by bound subquery nodes
    /// instead of substituting values into a rebuilt tree, so the executor never reconstructs the
    /// language package's AST nodes.
    /// </summary>
    private readonly SqlSubqueryValues? _subqueryValues;

    /// <summary>The engine's function catalog and the database, which calls bind against.</summary>
    private readonly SqlFunctionEnvironment _functions;

    /// <summary>The executing statement's cancellation token, which a called function's context carries.</summary>
    private readonly CancellationToken _cancellationToken;

    internal SqlExpressionEvaluator(IReadOnlyList<SqlCatalogColumn> columns, IReadOnlyDictionary<string, object?>? parameters,
        IReadOnlyList<SqlTableBinding>? bindings = null,
        IReadOnlyDictionary<SqlExpression, int>? valueOrdinals = null,
        Collation? defaultCollation = null,
        IReadOnlyDictionary<SqlExpression, SqlProjection>? projectionSources = null,
        IReadOnlyDictionary<SqlExpression, SqlSubquerySlot>? subquerySlots = null,
        SqlSubqueryValues? subqueryValues = null,
        SqlFunctionEnvironment? functions = null,
        CancellationToken cancellationToken = default)
    {
        _columns = columns;
        _parameters = parameters;
        _bindings = bindings;
        _valueOrdinals = valueOrdinals;
        _defaultCollation = defaultCollation ?? Collation.Binary;
        _projectionSources = projectionSources;
        _subquerySlots = subquerySlots;
        _subqueryValues = subqueryValues;
        _functions = functions ?? SqlFunctionEnvironment.Standard;
        _cancellationToken = cancellationToken;
    }

    /// <summary>
    /// Gets the evaluator an executing plan evaluates its bound expressions with. Bound nodes carry
    /// their ordinals, values, collations and functions, so it needs no scope: only the statement's
    /// materialized subquery values, which bound subquery nodes read, and the statement's
    /// cancellation token, which a called function's context carries.
    /// </summary>
    /// <param name="subqueryValues">The statement's materialized subquery values, or null when it has none.</param>
    /// <param name="cancellationToken">The statement's cancellation token.</param>
    /// <returns>
    /// The evaluator; a shared instance when there are no subquery values and the token cannot be
    /// canceled, so such a statement allocates none.
    /// </returns>
    internal static SqlExpressionEvaluator ForExecution(SqlSubqueryValues? subqueryValues, CancellationToken cancellationToken = default)
        => subqueryValues is null && !cancellationToken.CanBeCanceled
            ? RowEvaluator
            : new SqlExpressionEvaluator(Array.Empty<SqlCatalogColumn>(), null, subqueryValues: subqueryValues, cancellationToken: cancellationToken);

    /// <summary>Gets the statement's materialized subquery values, which a deferred collation reads.</summary>
    internal SqlSubqueryValues? SubqueryValues => _subqueryValues;

    /// <summary>Gets the engine's function catalog and the database that calls in this scope bind against.</summary>
    internal SqlFunctionEnvironment Functions => _functions;

    /// <summary>
    /// Creates the scope ordering keys bind in: references to ordering aliases and ordinals resolve
    /// to the completed output slots that follow the source row, while other keys keep the source
    /// scope, and each output slot takes its projection's collation.
    /// </summary>
    /// <param name="projections">The plan's projections, in output order.</param>
    /// <param name="outputSlots">The ORDER BY nodes bound to output ordinals, or null when none are.</param>
    /// <param name="projectionStart">The ordinal of the first output slot: the source row's width.</param>
    /// <returns>The ordering scope; this scope when no ordering node names an output.</returns>
    internal SqlExpressionEvaluator ForOrdering(IReadOnlyList<SqlProjection> projections,
        IReadOnlyDictionary<SqlExpression, int>? outputSlots, int projectionStart)
    {
        if (outputSlots is null || outputSlots.Count == 0)
        {
            return this;
        }
        var ordinals = _valueOrdinals is null ? new Dictionary<SqlExpression, int>()
            : new Dictionary<SqlExpression, int>(_valueOrdinals);
        var sources = new Dictionary<SqlExpression, SqlProjection>();
        foreach (var (expression, index) in outputSlots)
        {
            ordinals[expression] = projectionStart + index;
            sources[expression] = projections[index];
        }
        return new SqlExpressionEvaluator(_columns, _parameters, _bindings, ordinals,
            _defaultCollation, sources, _subquerySlots, _subqueryValues, _functions, _cancellationToken);
    }

    /// <summary>
    /// Evaluates a bound predicate: true only when the expression evaluates to true
    /// (false and null both reject the row).
    /// </summary>
    internal bool Matches(SqlBoundExpression? predicate, object?[] row)
    {
        if (predicate is null)
        {
            return true;
        }

        return Evaluate(predicate, row) is true;
    }

    /// <summary>
    /// Binds an expression in this scope and evaluates it once: the planner's way to compute a value
    /// before any row exists (a <c>LIMIT</c> count, a seek bound, a parameter's type). An expression
    /// a plan evaluates for every row is bound once instead (<see cref="Bind(SqlExpression)"/>).
    /// </summary>
    /// <exception cref="SqlEvaluationException">Division by zero or a numeric value out of range.</exception>
    /// <exception cref="DatabaseException">Any other evaluation error.</exception>
    internal object? Evaluate(SqlExpression expression, object?[] row) => Evaluate(Bind(expression), row);

    /// <summary>
    /// Evaluates a bound expression against a row. An arithmetic fault anywhere in the tree fails
    /// the statement with a coded <see cref="SqlEvaluationException"/>; a raw runtime
    /// <see cref="ArithmeticException"/> never escapes evaluation.
    /// </summary>
    /// <exception cref="SqlEvaluationException">Division by zero or a numeric value out of range.</exception>
    /// <exception cref="DatabaseException">Any other evaluation error.</exception>
    internal object? Evaluate(SqlBoundExpression expression, object?[] row)
    {
        try
        {
            return EvaluateCore(expression, row);
        }
        catch (ArithmeticException exception)
        {
            // Operators, functions and negation code their faults at the source; this
            // boundary codes the remainder (an oversized numeric literal, for example)
            // so no evaluation fault can surface as a non-database exception.
            throw SqlEvaluationException.FromArithmetic(exception);
        }
    }

    private object? EvaluateCore(SqlBoundExpression expression, object?[] row)
    {
        // Every operator recurses through here, once per level of the tree, never once per term
        // of an AND/OR chain. A tree the thread has too little stack left for fails the statement
        // instead of overflowing the stack (#1151).
        RuntimeHelpers.EnsureSufficientExecutionStack();

        switch (expression.Kind)
        {
            case SqlBoundExpressionKind.Column:
                return row[((SqlBoundColumn)expression).Ordinal];
            case SqlBoundExpressionKind.Constant:
                return ((SqlBoundConstant)expression).Value;
            case SqlBoundExpressionKind.Binary:
                return EvaluateBinary((SqlBoundBinary)expression, row);
            case SqlBoundExpressionKind.Logical:
                return EvaluateLogical((SqlBoundLogical)expression, row);
            case SqlBoundExpressionKind.Call:
                return EvaluateCall((SqlBoundCall)expression, row);
            case SqlBoundExpressionKind.Slot:
                return row[((SqlBoundSlot)expression).Ordinal];
            case SqlBoundExpressionKind.Parameter:
                return ((SqlBoundParameter)expression).Value;
            case SqlBoundExpressionKind.Unary:
                return EvaluateUnary((SqlBoundUnary)expression, row);
            case SqlBoundExpressionKind.IsNull:
                var isNull = (SqlBoundIsNull)expression;
                return Box(EvaluateCore(isNull.Operand, row) is null != isNull.IsNegated);
            case SqlBoundExpressionKind.Coalesce:
                return Coalesce(((SqlBoundCoalesce)expression).Arguments, row);
            case SqlBoundExpressionKind.Between:
                return EvaluateBetween((SqlBoundBetween)expression, row);
            case SqlBoundExpressionKind.In:
                return EvaluateIn((SqlBoundIn)expression, row);
            case SqlBoundExpressionKind.InSubquery:
                return EvaluateInSubquery((SqlBoundInSubquery)expression, row);
            case SqlBoundExpressionKind.Like:
                return EvaluateLike((SqlBoundLike)expression, row);
            case SqlBoundExpressionKind.Case:
                return EvaluateCase((SqlBoundCase)expression, row);
            case SqlBoundExpressionKind.Cast:
                return EvaluateCast((SqlBoundCast)expression, row);
            case SqlBoundExpressionKind.Subquery:
                return SqlSubqueryValues.Get(_subqueryValues, ((SqlBoundSubquery)expression).Slot)[0];
            case SqlBoundExpressionKind.Failure:
                return ((SqlBoundFailure)expression).Raise();
            default:
                throw new DatabaseException($"Bound expression '{expression.Kind}' is not supported by the executor yet.");
        }
    }

    /// <summary>Returns the shared box of a Boolean result.</summary>
    private static object Box(bool value) => value ? True : False;

    internal int ResolveColumn(SqlColumnReferenceExpression column)
    {
        if (_bindings is not null)
        {
            int resolved = -1;
            foreach (var binding in _bindings)
            {
                if (!MatchesQualifier(binding, column))
                {
                    continue;
                }

                for (int i = 0; i < binding.Table.Columns.Count; i++)
                {
                    if (!string.Equals(binding.Table.Columns[i].Name, column.ColumnName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (resolved >= 0)
                    {
                        throw new DatabaseException($"Ambiguous column '{ColumnName(column)}'; qualify it with a table name or alias.");
                    }

                    resolved = binding.Offset + i;
                }
            }

            return resolved >= 0 ? resolved : throw new DatabaseException($"Unknown column '{ColumnName(column)}'.");
        }

        for (int i = 0; i < _columns.Count; i++)
        {
            if (string.Equals(_columns[i].Name, column.ColumnName, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        throw new DatabaseException($"Unknown column '{column.ColumnName}'.");
    }

    /// <summary>
    /// Resolves a column reference to its declared type, or null when the evaluator's scope
    /// carries no column metadata for it.
    /// </summary>
    /// <exception cref="DatabaseException">The column is unknown or ambiguous.</exception>
    internal DatabaseType? ResolveColumnType(SqlColumnReferenceExpression column)
    {
        int ordinal = ResolveColumn(column);
        return ordinal >= 0 && ordinal < _columns.Count ? _columns[ordinal].Type.Type : null;
    }

    /// <summary>
    /// Resolves the collation two expressions compare under, explicit expression, column, then
    /// database collation in that order: the planner's question, asked once per statement. A
    /// subquery is opaque here, as it was while planning: its values' collation applies once the
    /// expression is bound (<see cref="Bind(SqlExpression)"/>).
    /// </summary>
    internal Collation ResolveCollation(SqlExpression? expression, SqlExpression? other = null)
        => SqlCollationCandidate.Choose(FindCollation(expression, bound: false), FindCollation(other, bound: false))
            ?? _defaultCollation;

    /// <summary>Resolves a directly projected column against the database default.</summary>
    internal Collation ResolveColumnCollation(int ordinal) => _columns[ordinal].Collation ?? _defaultCollation;

    /// <summary>The collation a column contributes: 2 when declared on the column, 1 when inherited.</summary>
    private SqlCollationCandidate ColumnCollation(int ordinal)
        => new(ResolveColumnCollation(ordinal), _columns[ordinal].Collation is null ? 1 : 2);

    /// <summary>
    /// Finds the collation an expression contributes to a comparison, and how strongly it binds,
    /// by walking the expression.
    /// </summary>
    /// <param name="expression">The expression, or null for none.</param>
    /// <param name="bound">
    /// <see langword="false"/> for the planner's question: a subquery is opaque, and a column or
    /// collation that does not resolve throws now. <see langword="true"/> for a bound expression,
    /// which evaluation compares: a subquery contributes its values' collation, and a failure is
    /// deferred to the comparison that would have raised it.
    /// </param>
    /// <returns>The candidate.</returns>
    private SqlCollationCandidate FindCollation(SqlExpression? expression, bool bound)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        if (expression is null)
        {
            return default;
        }

        // A materialized subquery carries its child column's collation on each value.
        if (bound && expression is SqlSubqueryExpression or SqlExistsExpression or SqlInExpression { Subquery: not null }
            && _subquerySlots is not null && _subquerySlots.TryGetValue(expression, out var slot))
        {
            return SubqueryCollation(slot, slot.Kind == SqlSubqueryKind.Set ? FindCollationCore(expression, bound) : default);
        }

        return FindCollationCore(expression, bound);
    }

    private SqlCollationCandidate FindCollationCore(SqlExpression expression, bool bound)
    {
        if (_projectionSources is not null && _projectionSources.TryGetValue(expression, out var source))
        {
            return source.ColumnOrdinal is int ordinal ? ColumnCollation(ordinal) : FindCollation(source.Expression, bound);
        }
        if (expression is SqlCollateExpression collate)
        {
            return CollateCollation(FindCollation(collate.Operand, bound), collate.CollationName, bound);
        }
        if (expression is SqlColumnReferenceExpression column)
        {
            return ColumnReferenceCollation(column, bound);
        }

        // CASE conditions select a result; their collation does not describe that result.
        var children = expression is SqlCaseExpression conditional
            ? conditional.WhenClauses.Select(clause => clause.Result).Concat(
                conditional.ElseResult is null ? [] : new[] { conditional.ElseResult })
            : SqlPlanner.Children(expression);
        var candidates = new List<SqlCollationCandidate>();
        foreach (var child in children)
        {
            candidates.Add(FindCollation(child, bound));
        }
        return SqlCollationCandidate.Fold(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(candidates));
    }

    /// <summary>
    /// The collation a materialized subquery contributes: a scalar's value carries the subquery
    /// column's collation, an <c>EXISTS</c>'s Boolean none, and an <c>IN</c> subquery's values the
    /// column's collation when it returned rows and the IN's operand's otherwise.
    /// </summary>
    /// <param name="slot">The subquery's slot.</param>
    /// <param name="unmaterialized">
    /// For an <c>IN</c> subquery, the collation the node contributes when the subquery returned no
    /// rows; unused for the other kinds.
    /// </param>
    private static SqlCollationCandidate SubqueryCollation(SqlSubquerySlot slot, SqlCollationCandidate unmaterialized) => slot.Kind switch
    {
        SqlSubqueryKind.Scalar => new(slot.Collation, 2),
        SqlSubqueryKind.Exists => default,
        _ => SqlCollationCandidate.Deferred(new SqlCollationSubqueryTerm(slot.Id, slot.Collation, unmaterialized)),
    };

    /// <summary>An explicit <c>COLLATE</c>: an inner explicit collation still wins.</summary>
    private static SqlCollationCandidate CollateCollation(SqlCollationCandidate operand, string name, bool bound)
    {
        if (operand.IsDeferred)
        {
            // The operand decides at run time whether the clause applies; the clause's own
            // collation is resolved now, so the comparison does not look it up by name per row.
            Collation? collation;
            try
            {
                collation = Collation.FromName(name);
            }
            catch (Exception exception) when (IsDeferrable(exception))
            {
                collation = null;
            }

            return SqlCollationCandidate.Deferred(new SqlCollationCollateTerm(operand, collation, name));
        }
        if (operand.Priority == 3)
        {
            return operand;
        }
        if (!bound)
        {
            return new(Collation.FromName(name), 3);
        }

        try
        {
            return new(Collation.FromName(name), 3);
        }
        catch (Exception exception) when (IsDeferrable(exception))
        {
            return CollationFailure(name);
        }
    }

    /// <summary>A column reference's collation; one that does not resolve fails now or, bound, when compared.</summary>
    private SqlCollationCandidate ColumnReferenceCollation(SqlColumnReferenceExpression column, bool bound)
    {
        if (!bound)
        {
            return ColumnCollation(ResolveColumn(column));
        }

        try
        {
            return ColumnCollation(ResolveColumn(column));
        }
        catch (Exception exception) when (IsDeferrable(exception))
        {
            return ColumnCollationFailure(column);
        }
    }

    // Deferred collation failures are built by factories, so the closure over the name or column
    // is allocated only when binding defers one, not on every call (see Bind's remarks).

    private static SqlCollationCandidate CollationFailure(string name)
        => SqlCollationCandidate.Deferred(new SqlCollationFailureTerm(() => Collation.FromName(name)));

    private SqlCollationCandidate ColumnCollationFailure(SqlColumnReferenceExpression column)
        => SqlCollationCandidate.Deferred(new SqlCollationFailureTerm(() => ResolveColumn(column)));

    /// <summary>
    /// Whether binding defers an exception to evaluation rather than raising it: every exception but
    /// an exhausted stack or memory, which fail the statement wherever they happen.
    /// </summary>
    private static bool IsDeferrable(Exception exception)
        => exception is not (InsufficientExecutionStackException or OutOfMemoryException);

    /// <summary>An alias replaces the base relation name within the join scope.</summary>
    private static bool MatchesQualifier(SqlTableBinding binding, SqlColumnReferenceExpression column)
    {
        if (column.TableAlias is null)
        {
            return column.SchemaName is null;
        }

        if (binding.Reference.Alias is { } alias)
        {
            return column.SchemaName is null && string.Equals(alias, column.TableAlias, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(binding.Table.Name, column.TableAlias, StringComparison.OrdinalIgnoreCase)
            && (column.SchemaName is null || string.Equals(binding.Table.Schema, column.SchemaName, StringComparison.OrdinalIgnoreCase));
    }

    private static string ColumnName(SqlColumnReferenceExpression column)
        => string.Join('.', new[] { column.SchemaName, column.TableAlias, column.ColumnName }.Where(part => part is not null));

    /// <summary>The magnitude of the BIGINT minimum, which only a negated literal can spell.</summary>
    private const ulong BigIntMinimumMagnitude = 9223372036854775808UL;

    /// <remarks>
    /// A numeric literal its type cannot hold raises <see cref="OverflowException"/> with
    /// the literal's text. <see cref="Evaluate(SqlBoundExpression, object?[])"/> codes it as out of
    /// range, and a CAST operand still reports it as a conversion failure. Binding parses each
    /// literal once; one that does not parse raises this when it is evaluated.
    /// </remarks>
    private static object? EvaluateLiteral(SqlLiteralExpression literal)
    {
        return literal.LiteralType switch
        {
            SqlLiteralType.Null => null,
            SqlLiteralType.String => literal.Value,
            SqlLiteralType.Boolean => Box(literal.Value.Equals("TRUE", StringComparison.OrdinalIgnoreCase)),
            SqlLiteralType.Integer => ParseIntegerLiteral(literal.Value),
            SqlLiteralType.Float => ParseFractionalLiteral(literal.Value),
            _ => throw new DatabaseException($"Literal type {literal.LiteralType} is not supported."),
        };
    }

    private static long ParseIntegerLiteral(string text)
    {
        try
        {
            return long.Parse(text, CultureInfo.InvariantCulture);
        }
        catch (OverflowException exception)
        {
            throw new OverflowException($"integer literal {text} exceeds BIGINT.", exception);
        }
    }

    private static decimal ParseFractionalLiteral(string text)
    {
        try
        {
            return SqlCastConverter.ParseNumericLiteral(text);
        }
        catch (OverflowException exception)
        {
            throw new OverflowException($"numeric literal {text} cannot be represented exactly as DECIMAL.", exception);
        }
    }

    /// <summary>
    /// Reports operand range/precision errors in the context of the requested conversion.
    /// Arithmetic inside the operand keeps its own coded fault: a division by zero or an
    /// overflowing operator is not a conversion failure.
    /// </summary>
    private object? EvaluateCast(SqlBoundCast cast, object?[] row)
    {
        try
        {
            return SqlCastConverter.Convert(EvaluateCore(cast.Operand, row), cast.Cast);
        }
        catch (Exception exception) when (exception is FormatException or OverflowException)
        {
            throw new DatabaseException($"CAST to {cast.Cast.TargetType} failed: operand cannot be represented exactly.", exception);
        }
    }

    /// <summary>
    /// Gets the value the statement supplied for a parameter, which lets the planner check what
    /// the value alone decides — such as a sign's operand type — before any row is read.
    /// </summary>
    /// <param name="parameter">The parameter reference.</param>
    /// <param name="value">The supplied value, which may be null.</param>
    /// <returns><see langword="true"/> when the statement supplied a value for the parameter.</returns>
    internal bool TryGetParameterValue(SqlParameterExpression parameter, out object? value)
    {
        // The AST keeps the sigil ('@name' / '$1'); callers bind by bare name.
        string name = parameter.ParameterName.TrimStart('@', '$');
        value = null;
        return _parameters is not null &&
            (_parameters.TryGetValue(name, out value) || _parameters.TryGetValue(parameter.ParameterName, out value));
    }

    /// <summary>
    /// Whether a sign accepts a non-null value: unary minus negates the signed numeric types, and
    /// unary plus returns any numeric value unchanged.
    /// </summary>
    /// <param name="sign"><see cref="SqlUnaryOperator.Negate"/> or <see cref="SqlUnaryOperator.Plus"/>.</param>
    /// <param name="value">The operand value.</param>
    /// <returns><see langword="true"/> when the sign evaluates over the value.</returns>
    internal static bool IsSignOperand(SqlUnaryOperator sign, object value) => sign == SqlUnaryOperator.Plus
        ? value is sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal
        : value is sbyte or short or int or long or float or double or decimal;

    /// <summary>
    /// Evaluates an <c>AND</c> or <c>OR</c> chain with SQL three-valued logic over nullable
    /// Booleans, term by term from the first (#1151): a chain of any length costs one level of
    /// recursion, not one per term.
    /// </summary>
    /// <remarks>
    /// The first term that decides the chain (<see langword="false"/> for <c>AND</c>,
    /// <see langword="true"/> for <c>OR</c>) ends it, and no later term is evaluated, so a guard
    /// such as <c>d &lt;&gt; 0 AND x / d &gt; 1</c> never divides by zero. Otherwise the chain is
    /// unknown when any term was unknown (a NULL, or a value that is not a Boolean), and the
    /// operator's identity when none was. That is exactly what the nested binary operators the
    /// chain replaces computed, in the same order and with the same faults: evaluating
    /// <c>(a AND b) AND c</c> left operand first visits a, b, c in turn and stops at the same
    /// term (the #1069 short-circuit contract).
    /// </remarks>
    private object? EvaluateLogical(SqlBoundLogical logical, object?[] row)
    {
        bool deciding = logical.IsOr;
        bool unknown = false;
        var operands = logical.Operands;
        for (int index = 0; index < operands.Length; index++)
        {
            bool? value = EvaluateCore(operands[index], row) as bool?;
            if (value is null)
            {
                unknown = true;
            }
            else if (value.Value == deciding)
            {
                return Box(deciding);
            }
        }

        return unknown ? null : Box(!deciding);
    }

    private object? EvaluateBinary(SqlBoundBinary binary, object?[] row)
    {
        object? leftValue = EvaluateCore(binary.Left, row);
        object? rightValue = EvaluateCore(binary.Right, row);

        if (leftValue is null || rightValue is null)
        {
            return null; // SQL null propagation
        }

        var collation = binary.Collation.Resolve(_subqueryValues);

        return binary.Operator switch
        {
            SqlBinaryOperator.Equal => Box(Compare(leftValue, rightValue, collation) == 0),
            SqlBinaryOperator.NotEqual => Box(Compare(leftValue, rightValue, collation) != 0),
            SqlBinaryOperator.LessThan => Box(Compare(leftValue, rightValue, collation) < 0),
            SqlBinaryOperator.GreaterThan => Box(Compare(leftValue, rightValue, collation) > 0),
            SqlBinaryOperator.LessOrEqual => Box(Compare(leftValue, rightValue, collation) <= 0),
            SqlBinaryOperator.GreaterOrEqual => Box(Compare(leftValue, rightValue, collation) >= 0),
            SqlBinaryOperator.Add or SqlBinaryOperator.Subtract or SqlBinaryOperator.Multiply
                or SqlBinaryOperator.Divide or SqlBinaryOperator.Modulo => Arithmetic(binary.Operator, leftValue, rightValue),
            SqlBinaryOperator.Concat => Convert.ToString(leftValue, CultureInfo.InvariantCulture) + Convert.ToString(rightValue, CultureInfo.InvariantCulture),
            _ => throw new DatabaseException($"Operator {binary.Operator} is not supported by the executor yet."),
        };
    }

    private object? EvaluateUnary(SqlBoundUnary unary, object?[] row)
    {
        object? operand = EvaluateCore(unary.Operand, row);

        if (operand is null)
        {
            return null;
        }

        return unary.Operator switch
        {
            SqlUnaryOperator.Negate => operand switch
            {
                sbyte value => -(long)value,
                short value => -(long)value,
                int value => -(long)value,
                // Two's complement has no positive counterpart for the minimum BIGINT.
                long value => value == long.MinValue
                    ? throw SqlEvaluationException.NumericValueOutOfRange($"negating BIGINT {value.ToString(CultureInfo.InvariantCulture)} overflowed.")
                    : -value,
                double value => -value,
                float value => -(double)value,
                decimal value => -value,
                _ => throw SqlEvaluationException.InvalidOperandType("-", OperandTypeName(operand)),
            },
            // ISO unary plus: a number passes through with its type unchanged.
            SqlUnaryOperator.Plus => IsSignOperand(SqlUnaryOperator.Plus, operand)
                ? operand
                : throw SqlEvaluationException.InvalidOperandType("+", OperandTypeName(operand)),
            SqlUnaryOperator.Not => operand is bool flag ? Box(!flag) : throw new DatabaseException("NOT requires a boolean operand."),
            _ => throw new DatabaseException($"Unary operator {unary.Operator} is not supported."),
        };
    }

    /// <summary>Names a runtime value's type the way the dialect's types are named.</summary>
    internal static string OperandTypeName(object value) => value switch
    {
        string => nameof(DatabaseType.String),
        bool => nameof(DatabaseType.Boolean),
        byte[] => nameof(DatabaseType.Binary),
        DateOnly => nameof(DatabaseType.Date),
        TimeOnly => nameof(DatabaseType.Time),
        DateTime => nameof(DatabaseType.DateTime),
        DateTimeOffset => nameof(DatabaseType.DateTimeOffset),
        TimeSpan => nameof(DatabaseType.TimeSpan),
        Guid => nameof(DatabaseType.Guid),
        _ => value.GetType().Name,
    };

    private object? EvaluateBetween(SqlBoundBetween expression, object?[] row)
    {
        object? value = EvaluateCore(expression.Operand, row);
        object? lower = EvaluateCore(expression.Low, row);
        object? upper = EvaluateCore(expression.High, row);

        if (value is null || lower is null || upper is null)
        {
            return null;
        }

        bool between = Compare(value, lower, expression.LowCollation.Resolve(_subqueryValues)) >= 0
            && Compare(value, upper, expression.HighCollation.Resolve(_subqueryValues)) <= 0;
        return Box(between != expression.IsNegated);
    }

    private object? EvaluateIn(SqlBoundIn expression, object?[] row)
    {
        var candidates = expression.Candidates;

        // Membership of an empty set is FALSE, even for a NULL left operand.
        if (candidates.Length == 0)
        {
            return Box(expression.IsNegated);
        }

        object? value = EvaluateCore(expression.Operand, row);

        if (value is null)
        {
            return null;
        }

        bool hasUnknown = false;
        for (int index = 0; index < candidates.Length; index++)
        {
            object? candidateValue = EvaluateCore(candidates[index], row);

            if (candidateValue is null)
            {
                hasUnknown = true;
            }
            else if (Compare(value, candidateValue, expression.Collations[index].Resolve(_subqueryValues)) == 0)
            {
                return Box(!expression.IsNegated);
            }
        }

        // No match with a NULL candidate is UNKNOWN for both IN and NOT IN.
        // CHECK permits UNKNOWN; WHERE filters it out.
        return hasUnknown ? null : Box(expression.IsNegated);
    }

    private object? EvaluateInSubquery(SqlBoundInSubquery expression, object?[] row)
    {
        // A subquery contributes its whole result set as the candidate list, resolved
        // before the empty-set rule below applies: IN over a subquery that returned no
        // rows is an empty set, not a one-value set.
        var candidates = SqlSubqueryValues.Get(_subqueryValues, expression.Slot);

        // Membership of an empty set is FALSE, even for a NULL left operand.
        if (candidates.Length == 0)
        {
            return Box(expression.IsNegated);
        }

        object? value = EvaluateCore(expression.Operand, row);

        if (value is null)
        {
            return null;
        }

        bool hasUnknown = false;
        foreach (object? candidateValue in candidates)
        {
            if (candidateValue is null)
            {
                hasUnknown = true;
            }
            else if (Compare(value, candidateValue, expression.Collation.Resolve(_subqueryValues)) == 0)
            {
                return Box(!expression.IsNegated);
            }
        }

        return hasUnknown ? null : Box(expression.IsNegated);
    }

    private object? EvaluateLike(SqlBoundLike expression, object?[] row)
    {
        object? value = EvaluateCore(expression.Operand, row);
        object? pattern = EvaluateCore(expression.Pattern, row);

        if (value is null || pattern is null)
        {
            return null;
        }

        bool matches = LikeMatches(
            Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
            Convert.ToString(pattern, CultureInfo.InvariantCulture) ?? string.Empty,
            expression.Collation.Resolve(_subqueryValues));

        return Box(matches != expression.IsNegated);
    }

    private object? EvaluateCase(SqlBoundCase expression, object?[] row)
    {
        object? input = expression.Input is null ? null : EvaluateCore(expression.Input, row);

        foreach (var when in expression.Whens)
        {
            if (expression.Input is null)
            {
                if (EvaluateCore(when.Condition, row) is true)
                {
                    return EvaluateCore(when.Result, row);
                }
            }
            else
            {
                object? candidate = EvaluateCore(when.Condition, row);

                if (input is not null && candidate is not null && Compare(input, candidate,
                    when.Collation.Resolve(_subqueryValues)) == 0)
                {
                    return EvaluateCore(when.Result, row);
                }
            }
        }

        return expression.ElseResult is null ? null : EvaluateCore(expression.ElseResult, row);
    }

    /// <summary>
    /// Computes a scalar function call. The binder resolved the call to its function once, before
    /// any row was read (a call no overload accepts bound as a <see cref="SqlBoundFailure"/>), so each
    /// row evaluates the arguments, then makes one call through the function's
    /// <see cref="SqlScalarFunction.Invoke"/>, which returns NULL without calling a strict function
    /// over a NULL argument and codes what the function throws as <c>COHSQLE007</c>.
    /// </summary>
    /// <remarks>
    /// Every argument is evaluated before the call, as PostgreSQL's executor does. Only the values
    /// stay in this frame while the arguments recurse, never the call's buffer: the frame of a
    /// one-argument call, the shape every standard-library scalar has, holds no buffer at all, so a
    /// deep nest of calls costs this walk no more stack per level than before. It is kept out
    /// of line, so its locals never join the frame of <see cref="EvaluateCore"/>, which every
    /// node of every tree recurses through.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private object? EvaluateCall(SqlBoundCall call, object?[] row)
    {
        var arguments = call.Arguments;
        switch (arguments.Length)
        {
            case 0:
                return InvokeScalar(call, []);
            case 1:
                object? value = EvaluateCore(arguments[0], row);
                return InvokeScalar(call, new ReadOnlySpan<object?>(in value));
            default:
                return EvaluateCallArguments(call, row);
        }
    }

    /// <summary>Evaluates the arguments of a call with more than one, then calls it.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private object? EvaluateCallArguments(SqlBoundCall call, object?[] row)
    {
        var arguments = call.Arguments;
        if (arguments.Length <= SqlObjectBuffer.Length)
        {
            SqlObjectBuffer buffer = default;
            Span<object?> values = buffer[..arguments.Length];
            for (int index = 0; index < values.Length; index++)
            {
                values[index] = EvaluateCore(arguments[index], row);
            }

            return InvokeScalar(call, values);
        }

        object?[] rented = ArrayPool<object?>.Shared.Rent(arguments.Length);
        try
        {
            for (int index = 0; index < arguments.Length; index++)
            {
                rented[index] = EvaluateCore(arguments[index], row);
            }

            return InvokeScalar(call, rented.AsSpan(0, arguments.Length));
        }
        finally
        {
            Array.Clear(rented, 0, arguments.Length);
            ArrayPool<object?>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Converts a call's evaluated arguments to the value ABI, in an inline buffer of four (a pooled
    /// one past four), each to its parameter's type, and calls the function once.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private object? InvokeScalar(SqlBoundCall call, ReadOnlySpan<object?> values)
    {
        var context = new SqlFunctionContext(call.Database, call.Collation.Resolve(_subqueryValues), _cancellationToken);
        if (values.Length == 1)
        {
            // The shape of every standard-library scalar: one value, no buffer to clear.
            SqlValue value = default;
            ConvertArguments(call, values, new Span<SqlValue>(ref value));
            return call.Function.Invoke(new SqlArguments(new ReadOnlySpan<SqlValue>(in value), context)).ToObject();
        }
        if (values.Length > SqlValueBuffer.Length)
        {
            return InvokeScalarPooled(call, values);
        }

        SqlValueBuffer buffer = default;
        Span<SqlValue> arguments = buffer[..values.Length];
        ConvertArguments(call, values, arguments);
        return call.Function.Invoke(new SqlArguments(arguments, context)).ToObject();
    }

    private object? InvokeScalarPooled(SqlBoundCall call, ReadOnlySpan<object?> values)
    {
        SqlValue[] rented = ArrayPool<SqlValue>.Shared.Rent(values.Length);
        try
        {
            Span<SqlValue> arguments = rented.AsSpan(0, values.Length);
            ConvertArguments(call, values, arguments);
            var context = new SqlFunctionContext(call.Database, call.Collation.Resolve(_subqueryValues), _cancellationToken);
            return call.Function.Invoke(new SqlArguments(arguments, context)).ToObject();
        }
        finally
        {
            ArrayPool<SqlValue>.Shared.Return(rented, clearArray: true);
        }
    }

    /// <summary>Converts evaluated arguments to SQL values of their parameters' types.</summary>
    private static void ConvertArguments(SqlBoundCall call, ReadOnlySpan<object?> values, Span<SqlValue> arguments)
    {
        var targets = call.Targets;
        for (int index = 0; index < values.Length; index++)
        {
            var value = SqlValue.FromObject(values[index]);
            arguments[index] = targets is null || targets[index] == DatabaseType.Null
                ? value
                : SqlFunctionResolver.Coerce(value, targets[index], call.Function, index);
        }
    }

    /// <summary>The first non-NULL argument, evaluated left to right and no further.</summary>
    private object? Coalesce(SqlBoundExpression[] arguments, object?[] row)
    {
        for (int index = 0; index < arguments.Length; index++)
        {
            object? value = EvaluateCore(arguments[index], row);

            if (value is not null)
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>
    /// Uses the same non-null value order as grouping, sorting and extrema.
    /// </summary>
    internal static int Compare(object left, object right, Collation? collation = null)
        => SqlValueComparer.Compare(left, right, collation);

    /// <summary>
    /// Retains decimal arithmetic conversion independently of value comparison. An
    /// approximate operand outside Decimal's range, or a NaN or infinity, cannot take
    /// part in Decimal arithmetic and fails the statement as out of range. A nonzero
    /// approximate operand below Decimal's smallest step (1e-28) rounds to 0 like any
    /// other digit the conversion drops; <see cref="Arithmetic"/> rejects it as a divisor.
    /// </summary>
    private static decimal ToArithmeticNumber(object value)
    {
        try
        {
            return value switch
            {
                sbyte v => v,
                short v => v,
                int v => v,
                long v => v,
                float v => Convert.ToDecimal(v),
                double v => Convert.ToDecimal(v),
                decimal v => v,
                _ => throw new DatabaseException("Arithmetic requires numeric operands."),
            };
        }
        catch (OverflowException exception)
        {
            throw SqlEvaluationException.NumericValueOutOfRange(
                $"approximate operand {ApproximateText(value)} cannot be represented as DECIMAL for arithmetic.", exception);
        }
    }

    private static string ApproximateText(object value) => value is float single
        ? single.ToString("R", CultureInfo.InvariantCulture)
        : ((double)value).ToString("R", CultureInfo.InvariantCulture);

    private static bool IsArithmeticNumber(object value)
        => value is sbyte or short or int or long or float or double or decimal;

    private static bool IsExactInteger(object value) => value is sbyte or short or int or long;

    /// <summary>
    /// Applies a binary arithmetic operator. Exact integer operands compute in BIGINT
    /// with overflow checking; any approximate or decimal operand promotes both sides
    /// to Decimal. A zero divisor, a nonzero approximate divisor that Decimal cannot
    /// distinguish from zero, and a result outside the computed type fail the
    /// statement with their coded diagnostics; nothing wraps or saturates.
    /// </summary>
    private static object Arithmetic(SqlBinaryOperator op, object left, object right)
    {
        if (!IsArithmeticNumber(left) || !IsArithmeticNumber(right))
        {
            throw new DatabaseException("Arithmetic requires numeric operands.");
        }

        if (IsExactInteger(left) && IsExactInteger(right))
        {
            long a = Convert.ToInt64(left, CultureInfo.InvariantCulture);
            long b = Convert.ToInt64(right, CultureInfo.InvariantCulture);
            if (b == 0 && op is SqlBinaryOperator.Divide or SqlBinaryOperator.Modulo)
            {
                throw SqlEvaluationException.DivisionByZero(OperatorText(op));
            }

            try
            {
                return op switch
                {
                    SqlBinaryOperator.Add => checked(a + b),
                    SqlBinaryOperator.Subtract => checked(a - b),
                    SqlBinaryOperator.Multiply => checked(a * b),
                    // The minimum BIGINT divided by -1 has no BIGINT quotient.
                    SqlBinaryOperator.Divide => checked(a / b),
                    // Every integer is divisible by -1; computing it would trap on the
                    // minimum BIGINT, whose remainder is still zero.
                    _ => b == -1 ? 0L : a % b,
                };
            }
            catch (OverflowException exception)
            {
                throw SqlEvaluationException.NumericValueOutOfRange($"BIGINT '{OperatorText(op)}' overflowed.", exception);
            }
        }

        decimal x = ToArithmeticNumber(left);
        decimal y = ToArithmeticNumber(right);
        if (y == 0m && op is SqlBinaryOperator.Divide or SqlBinaryOperator.Modulo)
        {
            // Zero is judged on the operand as supplied. A nonzero REAL or DOUBLE below
            // Decimal's smallest step converts to 0, and dividing by it has no Decimal
            // result: that is out of range, not a division by zero.
            if (right is float single && single != 0f || right is double number && number != 0d)
            {
                throw SqlEvaluationException.NumericValueOutOfRange(
                    $"approximate divisor {ApproximateText(right)} underflows DECIMAL for '{OperatorText(op)}'.");
            }

            throw SqlEvaluationException.DivisionByZero(OperatorText(op));
        }

        try
        {
            return op switch
            {
                SqlBinaryOperator.Add => x + y,
                SqlBinaryOperator.Subtract => x - y,
                SqlBinaryOperator.Multiply => x * y,
                SqlBinaryOperator.Divide => x / y,
                _ => x % y,
            };
        }
        catch (OverflowException exception)
        {
            throw SqlEvaluationException.NumericValueOutOfRange($"DECIMAL '{OperatorText(op)}' overflowed.", exception);
        }
    }

    private static string OperatorText(SqlBinaryOperator op) => op switch
    {
        SqlBinaryOperator.Add => "+",
        SqlBinaryOperator.Subtract => "-",
        SqlBinaryOperator.Multiply => "*",
        SqlBinaryOperator.Divide => "/",
        _ => "%",
    };

    /// <summary>
    /// SQL LIKE with <c>%</c> (any run) and <c>_</c> (any one character), using the effective collation.
    /// </summary>
    /// <remarks>
    /// Matching recurses once per <c>%</c> it backtracks through (and, for the compatibility
    /// collation, once per character), so its depth follows the values, not the statement: a
    /// pattern of 100,000 <c>%a</c> segments over a long enough value nested that deep and
    /// overflowed the stack. Each step checks the stack first, so such a match fails its
    /// statement with <c>COHSQLE004</c> instead (#1151).
    /// </remarks>
    /// <exception cref="InsufficientExecutionStackException">The match needs more stack than the thread has left.</exception>
    internal static bool LikeMatches(string input, string pattern, Collation? collation = null)
    {
        collation ??= Collation.Binary;
        if (!collation.IsIndexBacked)
        {
            return LikeMatchesLegacy(input, pattern, collation);
        }
        input = collation.Normalize(input);
        pattern = collation.Normalize(pattern);
        return Matches(input.AsSpan(), pattern.AsSpan());

        static bool Matches(ReadOnlySpan<char> input, ReadOnlySpan<char> pattern)
        {
            RuntimeHelpers.EnsureSufficientExecutionStack();
            while (!pattern.IsEmpty)
            {
                char token = pattern[0];

                if (token == '%')
                {
                    // Collapse the wildcard, then try every suffix.
                    var rest = pattern[1..];

                    if (rest.IsEmpty)
                    {
                        return true;
                    }

                    for (int i = 0; i <= input.Length; i++)
                    {
                        if (i > 0 && i < input.Length && char.IsLowSurrogate(input[i]) && char.IsHighSurrogate(input[i - 1]))
                        {
                            continue;
                        }
                        if (Matches(input[i..], rest))
                        {
                            return true;
                        }
                    }

                    return false;
                }

                if (input.IsEmpty)
                {
                    return false;
                }

                if (token != '_' && input[0] != token)
                {
                    return false;
                }

                int consumed = token == '_' && input.Length > 1
                    && char.IsHighSurrogate(input[0]) && char.IsLowSurrogate(input[1]) ? 2 : 1;
                input = input[consumed..];
                pattern = pattern[1..];
            }

            return input.IsEmpty;
        }
    }

    /// <summary>Compatibility matching uses comparisons, never index keys, for legacy linguistic ordering.</summary>
    private static bool LikeMatchesLegacy(string input, string pattern, Collation collation)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        if (pattern.Length == 0)
        {
            return input.Length == 0;
        }
        if (pattern[0] == '%')
        {
            for (int i = 0; i <= input.Length; i++)
            {
                if (LikeMatchesLegacy(input[i..], pattern[1..], collation))
                {
                    return true;
                }
            }
            return false;
        }
        if (pattern[0] == '_')
        {
            return input.Length > 0 && LikeMatchesLegacy(input[1..], pattern[1..], collation);
        }
        int length = pattern.IndexOfAny(['%', '_']);
        length = length < 0 ? pattern.Length : length;
        for (int i = 0; i <= input.Length; i++)
        {
            if (collation.Compare(input[..i], pattern[..length]) == 0
                && LikeMatchesLegacy(input[i..], pattern[length..], collation))
            {
                return true;
            }
        }
        return false;
    }
}
