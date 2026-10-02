using System.Collections.Generic;
using System.Globalization;

using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>Whether a function computes one value per row or one value per group of rows.</summary>
internal enum SqlFunctionKind
{
    /// <summary>A scalar function: the evaluator computes it from one row's argument values.</summary>
    Scalar,

    /// <summary>An aggregate function: a grouping plan accumulates it over the rows of a group.</summary>
    Aggregate,
}

/// <summary>
/// Names an executable function, so the evaluator dispatches on a value instead of comparing the
/// written name for every row.
/// </summary>
internal enum SqlBuiltinFunction
{
    /// <summary><c>COALESCE</c>: the first non-NULL argument.</summary>
    Coalesce,

    /// <summary><c>UPPER</c>: a string in upper case.</summary>
    Upper,

    /// <summary><c>LOWER</c>: a string in lower case.</summary>
    Lower,

    /// <summary><c>LENGTH</c>: a value's length in characters.</summary>
    Length,

    /// <summary><c>ABS</c>: a number's magnitude.</summary>
    Abs,

    /// <summary><c>COUNT</c>: the number of rows, or of non-NULL values.</summary>
    Count,

    /// <summary><c>SUM</c>: the sum of the non-NULL values.</summary>
    Sum,

    /// <summary><c>AVG</c>: the mean of the non-NULL values.</summary>
    Avg,

    /// <summary><c>MIN</c>: the least non-NULL value.</summary>
    Min,

    /// <summary><c>MAX</c>: the greatest non-NULL value.</summary>
    Max,
}

/// <summary>
/// One executable function's signature: its name, whether it is a scalar or an aggregate, how
/// many arguments a call may pass, whether it accepts <c>*</c>, and the call forms a diagnostic
/// shows. Every reader of a function's shape reads it from here (see <see cref="SqlFunctionSignatures"/>).
/// </summary>
/// <remarks>
/// A call matches a signature only when its argument count lies within the signature's bounds,
/// PostgreSQL's first test of a candidate (<c>func_get_detail</c> in
/// <c>src/backend/parser/parse_func.c</c>): a call no candidate accepts by count is
/// <c>function ... does not exist</c>, SQLSTATE 42883, with the detail "No function of that name
/// accepts the given number of arguments." Argument-type rules, result types, the NULL rule and
/// determinism (#1120) are further members of this class, not another table.
/// </remarks>
internal sealed class SqlFunctionSignature
{
    /// <summary>Initializes a function signature.</summary>
    /// <param name="function">The function the evaluator dispatches on.</param>
    /// <param name="name">The canonical, upper-case name.</param>
    /// <param name="kind">Whether the function is a scalar or an aggregate.</param>
    /// <param name="minimumArguments">The fewest arguments a call may pass.</param>
    /// <param name="maximumArguments">The most arguments a call may pass, or null when there is no limit.</param>
    /// <param name="acceptsStar">Whether <c>*</c> may be the call's only argument, as in <c>COUNT(*)</c>.</param>
    /// <param name="usage">The accepted call forms, as a diagnostic shows them.</param>
    internal SqlFunctionSignature(SqlBuiltinFunction function, string name, SqlFunctionKind kind,
        int minimumArguments, int? maximumArguments, bool acceptsStar, string usage)
    {
        Function = function;
        Name = name;
        Kind = kind;
        MinimumArguments = minimumArguments;
        MaximumArguments = maximumArguments;
        AcceptsStar = acceptsStar;
        Usage = usage;
    }

    /// <summary>Gets the function the evaluator dispatches on.</summary>
    internal SqlBuiltinFunction Function { get; }

    /// <summary>Gets the canonical, upper-case name. Calls match it case-insensitively.</summary>
    internal string Name { get; }

    /// <summary>Gets whether the function is a scalar or an aggregate.</summary>
    internal SqlFunctionKind Kind { get; }

    /// <summary>Gets the fewest arguments a call may pass.</summary>
    internal int MinimumArguments { get; }

    /// <summary>Gets the most arguments a call may pass, or null when any number from the minimum up is accepted.</summary>
    internal int? MaximumArguments { get; }

    /// <summary>Gets whether <c>*</c> may be the call's only argument. Only <c>COUNT(*)</c> accepts it.</summary>
    internal bool AcceptsStar { get; }

    /// <summary>Gets the accepted call forms, for example <c>ABS(numeric)</c>.</summary>
    internal string Usage { get; }

    /// <summary>Whether a call's arguments match this signature.</summary>
    /// <param name="arguments">The call's arguments.</param>
    /// <returns>
    /// <see langword="true"/> when the call passes <c>*</c> alone and the function accepts it, or
    /// passes no <c>*</c> and a number of arguments within the bounds.
    /// </returns>
    internal bool Accepts(IReadOnlyList<SqlExpression> arguments)
    {
        if (HasStar(arguments))
        {
            return AcceptsStar && arguments.Count == 1;
        }

        return arguments.Count >= MinimumArguments && (MaximumArguments is not { } maximum || arguments.Count <= maximum);
    }

    /// <summary>Describes the argument counts a call may pass, as a diagnostic words them.</summary>
    /// <returns>For example <c>exactly 1 argument</c>, <c>1 or more arguments</c> or <c>exactly 1 argument or '*'</c>.</returns>
    internal string DescribeArity()
    {
        string counts = MaximumArguments switch
        {
            null when MinimumArguments == 0 => "any number of arguments",
            null => $"{Count(MinimumArguments)} or more arguments",
            0 => "no arguments",
            int maximum when maximum == MinimumArguments => $"exactly {Count(maximum)} {Arguments(maximum)}",
            int maximum => $"{Count(MinimumArguments)} to {Count(maximum)} arguments",
        };

        return AcceptsStar ? $"{counts} or '*'" : counts;

        static string Count(int count) => count.ToString(CultureInfo.InvariantCulture);
        static string Arguments(int count) => count == 1 ? "argument" : "arguments";
    }

    /// <summary>Describes what a call passed, as a diagnostic words it.</summary>
    /// <param name="arguments">The call's arguments.</param>
    /// <returns><c>'*'</c>, <c>none</c>, or the number of arguments.</returns>
    internal static string DescribeArguments(IReadOnlyList<SqlExpression> arguments)
        => HasStar(arguments) ? "'*'"
            : arguments.Count == 0 ? "none"
            : arguments.Count.ToString(CultureInfo.InvariantCulture);

    private static bool HasStar(IReadOnlyList<SqlExpression> arguments)
    {
        for (int index = 0; index < arguments.Count; index++)
        {
            if (arguments[index] is SqlStarExpression)
            {
                return true;
            }
        }

        return false;
    }
}
