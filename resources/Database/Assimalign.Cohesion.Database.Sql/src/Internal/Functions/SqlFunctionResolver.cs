using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// Chooses the overload a function call executes, once per statement, the way PostgreSQL's
/// <c>func_get_detail</c> and <c>func_select_candidate</c> do (<c>src/backend/parser/parse_func.c</c>),
/// over a deliberately narrow implicit lattice.
/// </summary>
/// <remarks>
/// <para>
/// <b>By count first.</b> The candidates are the overloads whose parameter count the call's
/// argument count matches, a variadic overload taking any number from its fixed parameters up. A
/// call <c>name(*)</c> matches only an aggregate without parameters, which nothing else calls:
/// <c>COUNT()</c> is not <c>COUNT(*)</c>, as in PostgreSQL. A call no candidate accepts by count is
/// <c>COHSQLE006</c>, the message naming the counts the function accepts and its call forms, as
/// before typed signatures.
/// </para>
/// <para>
/// <b>Then by type.</b> Each candidate costs the sum of its arguments' costs: nothing for an exact
/// type, the number of steps for an implicit widening along INT8 → INT16 → INT32 → INT64 → DECIMAL →
/// FLOAT64, or one step for FLOAT32 → FLOAT64 (nothing widens to FLOAT32, and nothing to or from
/// text), and more than any widening for a pseudo-type, so a concrete
/// overload is preferred to a polymorphic one. An argument whose type the plan cannot tell — a NULL
/// literal, a NULL parameter — matches any parameter for nothing; a grouping key or aggregate
/// result has its type over the input row. Every
/// <see cref="SqlType.AnyElement"/> argument of one candidate must have one type. The unique
/// cheapest candidate wins; none is <c>COHSQLE006</c>, several are <c>COHSQLE008</c> (SQLSTATE
/// 42725). When the candidates by count are a single overload over <see cref="SqlType.Any"/> and at
/// most one <see cref="SqlType.AnyElement"/>, as every standard-library scalar is, no type is needed
/// and none is computed, so resolving a deep nest of built-in calls stays linear.
/// </para>
/// </remarks>
internal static class SqlFunctionResolver
{
    /// <summary>The cost of a pseudo-type parameter: more than the longest widening.</summary>
    private const int polymorphicCost = 10;

    /// <summary>The implicit widening chain; an argument converts to any type after its own.</summary>
    private static readonly DatabaseType[] _lattice =
        [DatabaseType.Int8, DatabaseType.Int16, DatabaseType.Int32, DatabaseType.Int64, DatabaseType.Decimal, DatabaseType.Float64];

    /// <summary>
    /// Checks that some overload accepts the call's number of arguments, and its <c>*</c>.
    /// </summary>
    /// <param name="name">The function name as written.</param>
    /// <param name="overloads">The name's overloads.</param>
    /// <param name="arguments">The call's arguments.</param>
    /// <exception cref="SqlEvaluationException">No overload accepts them (<c>COHSQLE006</c>).</exception>
    internal static void CheckArity(string name, SqlFunction[] overloads, IReadOnlyList<SqlExpression> arguments)
    {
        foreach (var overload in overloads)
        {
            if (AcceptsShape(overload, arguments))
            {
                return;
            }
        }

        throw SqlEvaluationException.FunctionSignatureMismatch(name, DescribeArity(overloads), DescribeArguments(arguments), Usage(overloads));
    }

    /// <summary>
    /// Whether choosing among the call's candidates needs its arguments' types: anything but one
    /// candidate over <see cref="SqlType.Any"/> and at most one <see cref="SqlType.AnyElement"/>.
    /// </summary>
    /// <param name="overloads">The name's overloads.</param>
    /// <param name="arguments">The call's arguments.</param>
    /// <returns><see langword="true"/> when <see cref="Choose"/> needs the argument types.</returns>
    internal static bool NeedsArgumentTypes(SqlFunction[] overloads, IReadOnlyList<SqlExpression> arguments)
    {
        if (IsStarCall(arguments))
        {
            return false; // name(*) has one candidate, the parameterless aggregate, and no value to type
        }

        SqlFunction? candidate = null;
        foreach (var overload in overloads)
        {
            if (!AcceptsShape(overload, arguments))
            {
                continue;
            }
            if (candidate is not null)
            {
                return true;
            }

            candidate = overload;
        }

        if (candidate is null)
        {
            return false;
        }

        int elements = 0;
        for (int index = 0; index < arguments.Count; index++)
        {
            var parameter = candidate.ParameterAt(index);
            if (ReferenceEquals(parameter, SqlType.AnyElement))
            {
                elements++;
            }
            else if (!ReferenceEquals(parameter, SqlType.Any))
            {
                return true;
            }
        }

        return elements > 1;
    }

    /// <summary>Chooses the overload a call executes; <see cref="CheckArity"/> passed.</summary>
    /// <param name="name">The function name as written.</param>
    /// <param name="overloads">The name's overloads.</param>
    /// <param name="arguments">The call's arguments.</param>
    /// <param name="types">
    /// The arguments' static types (<see cref="DatabaseType.Null"/> when the plan cannot tell), or
    /// empty when <see cref="NeedsArgumentTypes"/> said none are needed.
    /// </param>
    /// <returns>The overload.</returns>
    /// <exception cref="SqlEvaluationException">
    /// No overload accepts the argument types (<c>COHSQLE006</c>), or several accept them equally well (<c>COHSQLE008</c>).
    /// </exception>
    internal static SqlFunction Choose(string name, SqlFunction[] overloads, IReadOnlyList<SqlExpression> arguments, ReadOnlySpan<DatabaseType> types)
    {
        SqlFunction? best = null;
        SqlFunction? tie = null;
        int bestCost = int.MaxValue;
        foreach (var overload in overloads)
        {
            if (!AcceptsShape(overload, arguments))
            {
                continue;
            }

            int cost = Cost(overload, types);
            if (cost < 0)
            {
                continue;
            }
            if (cost < bestCost)
            {
                best = overload;
                tie = null;
                bestCost = cost;
            }
            else if (cost == bestCost)
            {
                tie ??= overload;
            }
        }

        if (best is null)
        {
            throw SqlEvaluationException.FunctionArgumentTypeMismatch(name, DescribeTypes(types), Usage(overloads));
        }
        if (tie is not null)
        {
            throw SqlEvaluationException.AmbiguousFunctionCall(name, DescribeTypes(types), $"{best} and {tie}");
        }

        return best;
    }

    /// <summary>The static type of a call's result: its declared type, or the rule its function types it by.</summary>
    /// <param name="function">The function the call resolved to.</param>
    /// <param name="types">The arguments' static types; empty when they were not computed.</param>
    /// <returns>The result type; <see cref="DatabaseType.Null"/> when the plan cannot tell.</returns>
    internal static DatabaseType ResultType(SqlFunction function, ReadOnlySpan<DatabaseType> types)
    {
        var returnType = function.ReturnType;
        if (!returnType.IsPseudo)
        {
            return returnType.Storage.Type;
        }

        // AnyElement: the type of the AnyElement arguments, which Cost unified.
        var element = DatabaseType.Null;
        for (int index = 0; index < types.Length && element == DatabaseType.Null; index++)
        {
            if (HasParameterAt(function, index) && ReferenceEquals(function.ParameterAt(index), SqlType.AnyElement))
            {
                element = types[index];
            }
        }

        // ABS widens an exact integer to BIGINT and keeps any other type, a non-number included, as
        // it did before typed signatures (owner decision 67): ABS('x') is still typed TEXT, so a CASE
        // or SUM over it is refused while planning, and the call itself fails only when it runs.
        return function.ResultRule != SqlResultRule.NumericElement ? element : element switch
        {
            DatabaseType.Int8 or DatabaseType.Int16 or DatabaseType.Int32 or DatabaseType.Int64 => DatabaseType.Int64,
            _ => element,
        };
    }

    /// <summary>The storage type each argument converts to before the call, or null when none converts.</summary>
    /// <param name="function">The function the call resolved to.</param>
    /// <param name="count">The number of arguments.</param>
    /// <returns>
    /// The target per argument, <see cref="DatabaseType.Null"/> for a pseudo-type parameter, which
    /// takes the value as it is; null when every parameter is a pseudo-type.
    /// </returns>
    internal static DatabaseType[]? CoercionTargets(SqlFunction function, int count)
    {
        if (function.VariadicParameter is null)
        {
            // Fixed by the signature, so computed once with the function and shared by every call;
            // the array is never written after that.
            return function.FixedCoercionTargets;
        }

        return ComputeCoercionTargets(function, count);
    }

    /// <summary>The storage type each of a number of arguments converts to; see <see cref="CoercionTargets"/>.</summary>
    /// <param name="function">The function.</param>
    /// <param name="count">The number of arguments.</param>
    /// <returns>The targets, or null when every parameter is a pseudo-type.</returns>
    internal static DatabaseType[]? ComputeCoercionTargets(SqlFunction function, int count)
    {
        DatabaseType[]? targets = null;
        for (int index = 0; index < count; index++)
        {
            var parameter = function.ParameterAt(index);
            if (!parameter.IsPseudo)
            {
                targets ??= new DatabaseType[count];
                targets[index] = parameter.Storage.Type;
            }
        }

        return targets;
    }

    /// <summary>
    /// Converts an argument to its parameter's storage type before the call: a widening along the
    /// lattice, or an integer narrowing that keeps the value (an integer literal the planner typed
    /// by its magnitude, <c>5</c> for an INTEGER parameter).
    /// </summary>
    /// <param name="value">The argument.</param>
    /// <param name="target">The parameter's storage type.</param>
    /// <param name="function">The function, for the message.</param>
    /// <param name="index">The argument's position, for the message.</param>
    /// <returns>The converted argument; NULL and a value of the target type unchanged.</returns>
    /// <exception cref="OverflowException">An integer does not fit the parameter's type; the evaluator codes it <c>COHSQLE002</c>.</exception>
    /// <exception cref="DatabaseException">The value's type does not convert to the parameter's.</exception>
    internal static SqlValue Coerce(in SqlValue value, DatabaseType target, SqlFunction function, int index)
    {
        // A JSON column's values are read as text, and a JSONB column's as bytes.
        if (value.IsNull || value.Type == target
            || target == DatabaseType.Json && value.Type == DatabaseType.String
            || target == DatabaseType.JsonBinary && value.Type == DatabaseType.Binary)
        {
            return value;
        }

        if (IsExactInteger(value.Type))
        {
            long number = value.Type switch
            {
                DatabaseType.Int8 => value.AsSByte(),
                DatabaseType.Int16 => value.AsInt16(),
                DatabaseType.Int32 => value.AsInt32(),
                _ => value.AsInt64(),
            };
            try
            {
                switch (target)
                {
                    case DatabaseType.Int8: return SqlValue.FromSByte(checked((sbyte)number));
                    case DatabaseType.Int16: return SqlValue.FromInt16(checked((short)number));
                    case DatabaseType.Int32: return SqlValue.FromInt32(checked((int)number));
                    case DatabaseType.Int64: return SqlValue.FromInt64(number);
                    case DatabaseType.Decimal: return SqlValue.FromDecimal(number);
                    case DatabaseType.Float64: return SqlValue.FromDouble(number);
                }
            }
            catch (OverflowException exception)
            {
                throw new OverflowException(
                    $"{SqlType.NameOf(value.Type)} {number.ToString(CultureInfo.InvariantCulture)} does not fit argument {index + 1} " +
                    $"of function '{function.Name}', which is {SqlType.NameOf(target)}.", exception);
            }
        }
        else if (value.Type == DatabaseType.Decimal && target == DatabaseType.Float64)
        {
            return SqlValue.FromDouble((double)value.AsDecimal());
        }
        else if (value.Type == DatabaseType.Float32 && target == DatabaseType.Float64)
        {
            return SqlValue.FromDouble(value.AsSingle());
        }

        throw new DatabaseException(
            $"Function '{function.Name}' takes {function.ParameterAt(index).Name} for argument {index + 1}, but the value is {SqlType.NameOf(value.Type)}.");
    }

    /// <summary>
    /// Converts a function's result of another type to its declared type along an implicit
    /// widening (INTEGER to BIGINT, REAL to DOUBLE, ...), which loses nothing.
    /// </summary>
    /// <param name="value">The result, not NULL.</param>
    /// <param name="target">The declared storage type.</param>
    /// <param name="function">The function.</param>
    /// <param name="widened">The converted result.</param>
    /// <returns><see langword="false"/> when the result's type does not widen to the declared one.</returns>
    internal static bool TryWiden(in SqlValue value, DatabaseType target, SqlFunction function, out SqlValue widened)
    {
        if (Distance(value.Type, target) <= 0)
        {
            widened = default;
            return false;
        }

        widened = Coerce(value, target, function, 0); // a widening never overflows
        return true;
    }

    /// <summary>Whether an argument position has a parameter: always for a variadic function; the <c>*</c> of <c>name(*)</c> has none.</summary>
    private static bool HasParameterAt(SqlFunction function, int index)
        => index < function.ParameterTypes.Length || function.VariadicParameter is not null;

    /// <summary>Whether a call is <c>name(*)</c>.</summary>
    /// <param name="arguments">The call's arguments.</param>
    /// <returns><see langword="true"/> when <c>*</c> is the only argument.</returns>
    internal static bool IsStarCall(IReadOnlyList<SqlExpression> arguments)
        => arguments.Count == 1 && arguments[0] is SqlStarExpression;

    /// <summary>Whether an overload accepts the call's number of arguments and its <c>*</c>.</summary>
    /// <param name="overload">The overload.</param>
    /// <param name="arguments">The call's arguments.</param>
    /// <returns><see langword="true"/> when the overload is a candidate for the call by its shape.</returns>
    internal static bool AcceptsShape(SqlFunction overload, IReadOnlyList<SqlExpression> arguments)
    {
        bool parameterless = overload.ParameterTypes.Length == 0 && overload.VariadicParameter is null;
        if (IsStarCall(arguments))
        {
            return parameterless && overload.Kind == SqlFunctionKind.Aggregate;
        }
        for (int index = 0; index < arguments.Count; index++)
        {
            if (arguments[index] is SqlStarExpression)
            {
                return false;
            }
        }

        // A parameterless aggregate is called as name(*), never name().
        return !(arguments.Count == 0 && parameterless && overload.Kind == SqlFunctionKind.Aggregate)
            && overload.AcceptsCount(arguments.Count);
    }

    /// <summary>
    /// A candidate's cost for the argument types, or -1 when it does not accept them. A variadic
    /// candidate costs one more, so an overload of exactly the call's count is preferred to it.
    /// </summary>
    private static int Cost(SqlFunction overload, ReadOnlySpan<DatabaseType> types)
    {
        int cost = overload.VariadicParameter is null ? 0 : 1;
        var element = DatabaseType.Null;
        for (int index = 0; index < types.Length && HasParameterAt(overload, index); index++)
        {
            var parameter = overload.ParameterAt(index);
            var argument = types[index];
            if (parameter.IsPseudo)
            {
                if (ReferenceEquals(parameter, SqlType.AnyElement) && argument != DatabaseType.Null)
                {
                    if (element != DatabaseType.Null && element != argument)
                    {
                        return -1;
                    }

                    element = argument;
                }
                else if (ReferenceEquals(parameter, SqlType.AnyNumeric) && argument != DatabaseType.Null && !IsNumeric(argument))
                {
                    return -1;
                }

                cost += polymorphicCost;
                continue;
            }
            if (argument == DatabaseType.Null)
            {
                continue;
            }

            int distance = Distance(argument, parameter.Storage.Type);
            if (distance < 0)
            {
                return -1;
            }

            cost += distance;
        }

        return cost;
    }

    /// <summary>The steps of an implicit widening, 0 for the same type, -1 when there is none.</summary>
    private static int Distance(DatabaseType from, DatabaseType to)
    {
        if (from == to)
        {
            return 0;
        }
        if (from == DatabaseType.Float32)
        {
            // REAL widens to DOUBLE, losslessly, and to nothing else (PostgreSQL's implicit
            // float4 → float8). Nothing widens to REAL: every other conversion to it loses digits.
            return to == DatabaseType.Float64 ? 1 : -1;
        }

        int source = Array.IndexOf(_lattice, from);
        int target = Array.IndexOf(_lattice, to);
        return source >= 0 && target > source ? target - source : -1;
    }

    private static bool IsExactInteger(DatabaseType type)
        => type is DatabaseType.Int8 or DatabaseType.Int16 or DatabaseType.Int32 or DatabaseType.Int64;

    private static bool IsNumeric(DatabaseType type)
        => IsExactInteger(type) || type is DatabaseType.Float32 or DatabaseType.Float64 or DatabaseType.Decimal;

    /// <summary>The call forms of every overload, as a diagnostic lists them: <c>COUNT(*) or COUNT(value)</c>.</summary>
    private static string Usage(SqlFunction[] overloads) => string.Join(" or ", overloads.Select(overload => overload.Usage));

    /// <summary>
    /// Describes the argument counts the overloads accept, as a diagnostic words them: for example
    /// <c>exactly 1 argument</c>, <c>1 or more arguments</c>, <c>1 to 3 arguments</c> or
    /// <c>exactly 1 argument or '*'</c>.
    /// </summary>
    internal static string DescribeArity(SqlFunction[] overloads)
    {
        var counts = new SortedSet<int>();
        int? variadic = null;
        bool star = false;
        foreach (var overload in overloads)
        {
            int fixedCount = overload.ParameterTypes.Length;
            if (overload.VariadicParameter is not null)
            {
                variadic = Math.Min(variadic ?? int.MaxValue, fixedCount);
            }
            else if (fixedCount == 0 && overload.Kind == SqlFunctionKind.Aggregate)
            {
                star = true;
            }
            else
            {
                counts.Add(fixedCount);
            }
        }
        if (variadic is int fewest)
        {
            counts.RemoveWhere(count => count >= fewest);
        }

        string text;
        if (counts.Count == 0)
        {
            text = variadic switch
            {
                null => string.Empty,
                0 => "any number of arguments",
                int minimum => $"{Count(minimum)} or more arguments",
            };
        }
        else if (counts.Count == 1 && variadic is null)
        {
            int only = counts.Min;
            text = only == 0 ? "no arguments" : $"exactly {Count(only)} {(only == 1 ? "argument" : "arguments")}";
        }
        else
        {
            var listed = counts.Select(Count).ToList();
            if (variadic is null && counts.Max - counts.Min + 1 == counts.Count)
            {
                text = $"{listed[0]} to {listed[^1]} arguments";
            }
            else
            {
                if (variadic is int minimum)
                {
                    listed.Add($"{Count(minimum)} or more");
                }

                text = $"{string.Join(", ", listed.Take(listed.Count - 1))} or {listed[^1]} arguments";
            }
        }

        if (!star)
        {
            return text;
        }

        return text.Length == 0 ? "only '*'" : $"{text} or '*'";

        static string Count(int count) => count.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Describes what a call passed, as a diagnostic words it.</summary>
    /// <param name="arguments">The call's arguments.</param>
    /// <returns>
    /// <c>'*'</c> when <c>*</c> is the only argument, <c>none</c>, the number of arguments, or for
    /// example <c>2 arguments including '*'</c> when a <c>*</c> is one of several.
    /// </returns>
    internal static string DescribeArguments(IReadOnlyList<SqlExpression> arguments)
    {
        string count = arguments.Count.ToString(CultureInfo.InvariantCulture);
        bool star = false;
        for (int index = 0; index < arguments.Count; index++)
        {
            star |= arguments[index] is SqlStarExpression;
        }

        return arguments.Count switch
        {
            0 => "none",
            1 when star => "'*'",
            _ when star => $"{count} arguments including '*'",
            _ => count,
        };
    }

    private static string DescribeTypes(ReadOnlySpan<DatabaseType> types)
    {
        var text = new StringBuilder();
        for (int index = 0; index < types.Length; index++)
        {
            text.Append(index == 0 ? string.Empty : ", ").Append(SqlType.NameOf(types[index]));
        }

        return text.ToString();
    }
}
