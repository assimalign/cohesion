using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// Evaluates SQL scalar expressions against a row. Null propagates SQL-style:
/// any null operand makes a comparison or arithmetic result null, and a null
/// predicate result filters the row out.
/// </summary>
internal sealed class SqlExpressionEvaluator
{
    private readonly IReadOnlyList<SqlCatalogColumn> _columns;
    private readonly IReadOnlyDictionary<string, object?>? _parameters;
    private readonly IReadOnlyList<SqlTableBinding>? _bindings;
    private readonly IReadOnlyDictionary<SqlExpression, int>? _valueOrdinals;
    private readonly Collation _defaultCollation;
    private readonly IReadOnlyDictionary<SqlExpression, SqlProjection>? _projectionSources;

    /// <summary>
    /// Values materialized by the enclosing subquery plan, keyed by the slot the planner
    /// left in the expression tree. Resolved here rather than substituted into a rebuilt
    /// tree, so the executor never reconstructs the language package's AST nodes.
    /// </summary>
    private readonly IReadOnlyDictionary<SqlExpression, SqlExpression[]>? _subqueryValues;

    internal SqlExpressionEvaluator(IReadOnlyList<SqlCatalogColumn> columns, IReadOnlyDictionary<string, object?>? parameters,
        IReadOnlyList<SqlTableBinding>? bindings = null,
        IReadOnlyDictionary<SqlExpression, int>? valueOrdinals = null,
        Collation? defaultCollation = null,
        IReadOnlyDictionary<SqlExpression, SqlProjection>? projectionSources = null,
        IReadOnlyDictionary<SqlExpression, SqlExpression[]>? subqueryValues = null)
    {
        _columns = columns;
        _parameters = parameters;
        _bindings = bindings;
        _valueOrdinals = valueOrdinals;
        _defaultCollation = defaultCollation ?? Collation.Binary;
        _projectionSources = projectionSources;
        _subqueryValues = subqueryValues;
    }

    /// <summary>
    /// Resolves ordering references against materialized output slots while retaining
    /// the source scope for other keys and each projection's comparison collation.
    /// </summary>
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
            _defaultCollation, sources, _subqueryValues);
    }

    /// <summary>
    /// Evaluates a predicate: true only when the expression evaluates to true
    /// (false and null both reject the row).
    /// </summary>
    internal bool Matches(SqlExpression? predicate, object?[] row)
    {
        if (predicate is null)
        {
            return true;
        }

        return Evaluate(predicate, row) is true;
    }

    /// <summary>
    /// Evaluates a scalar expression against a row. An arithmetic fault anywhere in
    /// the tree fails the statement with a coded <see cref="SqlEvaluationException"/>;
    /// a raw runtime <see cref="ArithmeticException"/> never escapes evaluation.
    /// </summary>
    /// <exception cref="SqlEvaluationException">Division by zero or a numeric value out of range.</exception>
    /// <exception cref="DatabaseException">Any other evaluation error.</exception>
    internal object? Evaluate(SqlExpression expression, object?[] row)
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

    private object? EvaluateCore(SqlExpression expression, object?[] row)
    {
        // Every operator recurses through here, once per level of the tree, never once per term
        // of an AND/OR chain. A tree the thread has too little stack left for fails the statement
        // instead of overflowing the stack (#1151).
        RuntimeHelpers.EnsureSufficientExecutionStack();

        // A grouping plan binds complete key expressions and aggregate calls
        // to result slots; scalar expressions compose over those values.
        if (_valueOrdinals is not null && _valueOrdinals.TryGetValue(expression, out int ordinal))
        {
            return row[ordinal];
        }

        return expression switch
        {
            SqlConstantExpression constant => constant.Value,
            SqlSubqueryExpression or SqlExistsExpression => EvaluateCore(ResolveSubquery(expression)[0], row),
            SqlLiteralExpression literal => EvaluateLiteral(literal),
            SqlColumnReferenceExpression column => row[ResolveColumn(column)],
            SqlParameterExpression parameter => ResolveParameter(parameter),
            SqlLogicalExpression logical => EvaluateLogical(logical, row),
            SqlBinaryExpression binary => EvaluateBinary(binary, row),
            SqlUnaryExpression unary => EvaluateUnary(unary, row),
            SqlIsNullExpression isNull => EvaluateIsNull(isNull, row),
            SqlBetweenExpression between => EvaluateBetween(between, row),
            SqlInExpression inExpression => EvaluateIn(inExpression, row),
            SqlLikeExpression like => EvaluateLike(like, row),
            SqlCaseExpression caseExpression => EvaluateCase(caseExpression, row),
            SqlFunctionCallExpression function => EvaluateFunction(function, row),
            SqlCastExpression cast => EvaluateCast(cast, row),
            SqlCollateExpression collate => EvaluateCore(collate.Operand, row),
            _ => throw new DatabaseException($"Expression '{expression.GetType().Name}' is not supported by the executor yet."),
        };
    }

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

    /// <summary>Resolves explicit expression, column, then database collation in that order.</summary>
    internal Collation ResolveCollation(SqlExpression? expression, SqlExpression? other = null)
    {
        var first = FindCollation(expression);
        var second = FindCollation(other);
        return (second.Priority > first.Priority ? second.Collation : first.Collation) ?? _defaultCollation;
    }

    /// <summary>Resolves a directly projected column against the database default.</summary>
    internal Collation ResolveColumnCollation(int ordinal) => _columns[ordinal].Collation ?? _defaultCollation;

    private (Collation? Collation, int Priority) FindCollation(SqlExpression? expression)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();

        // A materialized subquery carries its child column's collation on each value.
        if (expression is SqlSubqueryExpression or SqlExistsExpression or SqlInExpression { Subquery: not null }
            && _subqueryValues is not null && _subqueryValues.TryGetValue(expression, out var materialized)
            && materialized.Length > 0)
        {
            return FindCollation(materialized[0]);
        }
        if (expression is SqlConstantExpression { Collation: not null } constant)
        {
            return (constant.Collation, 2);
        }
        if (expression is not null && _projectionSources is not null
            && _projectionSources.TryGetValue(expression, out var source))
        {
            return source.ColumnOrdinal is int ordinal
                ? (ResolveColumnCollation(ordinal), _columns[ordinal].Collation is null ? 1 : 2)
                : FindCollation(source.Expression);
        }
        if (expression is SqlCollateExpression collate)
        {
            var inner = FindCollation(collate.Operand);
            return inner.Priority == 3 ? inner : (Collation.FromName(collate.CollationName), 3);
        }
        if (expression is SqlColumnReferenceExpression column)
        {
            int ordinal = ResolveColumn(column);
            return (ResolveColumnCollation(ordinal), _columns[ordinal].Collation is null ? 1 : 2);
        }
        (Collation? Collation, int Priority) best = (null, 0);
        if (expression is not null)
        {
            // CASE conditions select a result; their collation does not describe that result.
            var children = expression is SqlCaseExpression conditional
                ? conditional.WhenClauses.Select(clause => clause.Result).Concat(
                    conditional.ElseResult is null ? [] : new[] { conditional.ElseResult })
                : SqlPlanner.Children(expression);
            foreach (var child in children)
            {
                var candidate = FindCollation(child);
                if (candidate.Priority > best.Priority)
                {
                    best = candidate;
                }
            }
        }
        return best;
    }

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
    /// the literal's text. <see cref="Evaluate"/> codes it as out of range, and a CAST
    /// operand still reports it as a conversion failure.
    /// </remarks>
    private static object? EvaluateLiteral(SqlLiteralExpression literal)
    {
        return literal.LiteralType switch
        {
            SqlLiteralType.Null => null,
            SqlLiteralType.String => literal.Value,
            SqlLiteralType.Boolean => literal.Value.Equals("TRUE", StringComparison.OrdinalIgnoreCase),
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
    private object? EvaluateCast(SqlCastExpression cast, object?[] row)
    {
        try
        {
            return SqlCastConverter.Convert(EvaluateCore(cast.Operand, row), cast);
        }
        catch (Exception exception) when (exception is FormatException or OverflowException)
        {
            throw new DatabaseException($"CAST to {cast.TargetType} failed: operand cannot be represented exactly.", exception);
        }
    }

    private object? ResolveParameter(SqlParameterExpression parameter)
        => TryGetParameterValue(parameter, out object? value)
            ? value
            : throw new DatabaseException($"No value was supplied for parameter '{parameter.ParameterName.TrimStart('@', '$')}'.");

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
    private object? EvaluateLogical(SqlLogicalExpression logical, object?[] row)
    {
        bool deciding = logical.Operator == SqlLogicalOperator.Or;
        bool unknown = false;
        var operands = logical.Operands;
        for (int index = 0; index < operands.Count; index++)
        {
            bool? value = EvaluateCore(operands[index], row) as bool?;
            if (value is null)
            {
                unknown = true;
            }
            else if (value.Value == deciding)
            {
                return deciding;
            }
        }

        return unknown ? null : !deciding;
    }

    private object? EvaluateBinary(SqlBinaryExpression binary, object?[] row)
    {
        object? leftValue = EvaluateCore(binary.Left, row);
        object? rightValue = EvaluateCore(binary.Right, row);

        if (leftValue is null || rightValue is null)
        {
            return null; // SQL null propagation
        }

        var collation = ResolveCollation(binary.Left, binary.Right);

        return binary.Operator switch
        {
            SqlBinaryOperator.Equal => Compare(leftValue, rightValue, collation) == 0,
            SqlBinaryOperator.NotEqual => Compare(leftValue, rightValue, collation) != 0,
            SqlBinaryOperator.LessThan => Compare(leftValue, rightValue, collation) < 0,
            SqlBinaryOperator.GreaterThan => Compare(leftValue, rightValue, collation) > 0,
            SqlBinaryOperator.LessOrEqual => Compare(leftValue, rightValue, collation) <= 0,
            SqlBinaryOperator.GreaterOrEqual => Compare(leftValue, rightValue, collation) >= 0,
            SqlBinaryOperator.Add or SqlBinaryOperator.Subtract or SqlBinaryOperator.Multiply
                or SqlBinaryOperator.Divide or SqlBinaryOperator.Modulo => Arithmetic(binary.Operator, leftValue, rightValue),
            SqlBinaryOperator.Concat => Convert.ToString(leftValue, CultureInfo.InvariantCulture) + Convert.ToString(rightValue, CultureInfo.InvariantCulture),
            _ => throw new DatabaseException($"Operator {binary.Operator} is not supported by the executor yet."),
        };
    }

    private object? EvaluateUnary(SqlUnaryExpression unary, object?[] row)
    {
        // The BIGINT minimum's magnitude is one past BIGINT's maximum, so its literal
        // cannot be parsed before the sign applies. A negated integer literal of exactly
        // that magnitude is read as the signed literal it spells.
        if (unary.Operator == SqlUnaryOperator.Negate
            && unary.Operand is SqlLiteralExpression { LiteralType: SqlLiteralType.Integer } magnitude
            && ulong.TryParse(magnitude.Value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong digits)
            && digits == BigIntMinimumMagnitude)
        {
            return long.MinValue;
        }

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
            SqlUnaryOperator.Not => operand is bool flag ? !flag : throw new DatabaseException("NOT requires a boolean operand."),
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

    private object? EvaluateIsNull(SqlIsNullExpression expression, object?[] row)
    {
        bool isNull = EvaluateCore(expression.Operand, row) is null;
        return expression.IsNegated ? !isNull : isNull;
    }

    private object? EvaluateBetween(SqlBetweenExpression expression, object?[] row)
    {
        object? value = EvaluateCore(expression.Operand, row);
        object? lower = EvaluateCore(expression.Low, row);
        object? upper = EvaluateCore(expression.High, row);

        if (value is null || lower is null || upper is null)
        {
            return null;
        }

        bool between = Compare(value, lower, ResolveCollation(expression.Operand, expression.Low)) >= 0
            && Compare(value, upper, ResolveCollation(expression.Operand, expression.High)) <= 0;
        return expression.IsNegated ? !between : between;
    }

    /// <summary>
    /// Resolves the values a subquery slot stands for. A slot reaching evaluation without
    /// its plan having materialized it is an executor bug, not a user error.
    /// </summary>
    private SqlExpression[] ResolveSubquery(SqlExpression source)
    {
        if (_subqueryValues is null || !_subqueryValues.TryGetValue(source, out var values))
        {
            throw new DatabaseException("A subquery must be materialized by its plan before scalar evaluation.");
        }

        return values;
    }

    private object? EvaluateIn(SqlInExpression expression, object?[] row)
    {
        // A subquery contributes its whole result set as the candidate list, resolved
        // before the empty-set rule below applies: IN over a subquery that returned no
        // rows is an empty set, not a one-value set.
        IReadOnlyList<SqlExpression> candidates = expression.Subquery is not null
            ? ResolveSubquery(expression)
            : expression.Values ?? throw new DatabaseException("An IN query requires a value list or a subquery.");

        // Membership of an empty set is FALSE, even for a NULL left operand.
        if (candidates.Count == 0)
        {
            return expression.IsNegated;
        }

        object? value = EvaluateCore(expression.Operand, row);

        if (value is null)
        {
            return null;
        }

        bool hasUnknown = false;
        foreach (var candidate in candidates)
        {
            object? candidateValue = EvaluateCore(candidate, row);

            if (candidateValue is null)
            {
                hasUnknown = true;
            }
            else if (Compare(value, candidateValue, ResolveCollation(expression.Operand, candidate)) == 0)
            {
                return !expression.IsNegated;
            }
        }

        // No match with a NULL candidate is UNKNOWN for both IN and NOT IN.
        // CHECK permits UNKNOWN; WHERE filters it out.
        return hasUnknown ? null : expression.IsNegated;
    }

    private object? EvaluateLike(SqlLikeExpression expression, object?[] row)
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
            ResolveCollation(expression.Operand, expression.Pattern));

        return expression.IsNegated ? !matches : matches;
    }

    private object? EvaluateCase(SqlCaseExpression expression, object?[] row)
    {
        object? input = expression.Input is null ? null : EvaluateCore(expression.Input, row);

        foreach (var when in expression.WhenClauses)
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
                    ResolveCollation(expression.Input, when.Condition)) == 0)
                {
                    return EvaluateCore(when.Result, row);
                }
            }
        }

        return expression.ElseResult is null ? null : EvaluateCore(expression.ElseResult, row);
    }

    private object? EvaluateFunction(SqlFunctionCallExpression function, object?[] row)
    {
        string name = function.FunctionName.ToUpperInvariant();

        if (name == "COALESCE")
        {
            foreach (var argument in function.Arguments)
            {
                object? value = EvaluateCore(argument, row);

                if (value is not null)
                {
                    return value;
                }
            }

            return null;
        }

        object? single = function.Arguments.Count == 1 ? EvaluateCore(function.Arguments[0], row) : null;

        return name switch
        {
            "UPPER" => (single as string)?.ToUpperInvariant() ?? single,
            "LOWER" => (single as string)?.ToLowerInvariant() ?? single,
            "LENGTH" => single is null ? null : (long)(Convert.ToString(single, CultureInfo.InvariantCulture)?.Length ?? 0),
            // Every numeric storage type: exact integers widen to BIGINT before the
            // magnitude is taken (so INT's minimum is representable), approximate and
            // decimal values keep their own type.
            "ABS" => single switch
            {
                null => null,
                sbyte value => Math.Abs((long)value),
                short value => Math.Abs((long)value),
                int value => Math.Abs((long)value),
                long value => value == long.MinValue
                    ? throw SqlEvaluationException.NumericValueOutOfRange($"ABS of BIGINT {value.ToString(CultureInfo.InvariantCulture)} overflowed.")
                    : Math.Abs(value),
                float value => Math.Abs(value),
                double value => Math.Abs(value),
                decimal value => Math.Abs(value),
                _ => throw new DatabaseException("ABS requires a numeric argument."),
            },
            _ => throw new DatabaseException($"Function '{function.FunctionName}' is not supported by the executor yet."),
        };
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
