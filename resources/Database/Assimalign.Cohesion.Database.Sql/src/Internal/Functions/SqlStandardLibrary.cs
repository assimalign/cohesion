using System;
using System.Collections.Generic;
using System.Linq;

using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// The SQL engine's built-in functions, and the rules every function name follows. The built-ins
/// are internal sealed leaves of the public function family, registered through the same
/// <see cref="SqlFunctionCollection.Add"/> an application calls, so the engine executes its own
/// functions exactly as it executes an application's (phase E2, owner decision 60 of 2026-10-09).
/// </summary>
/// <remarks>
/// <para>
/// T1 signatures reject incompatible inputs while planning, before reading any row (#1120).
/// </para>
/// <para>
/// The special forms (<c>COALESCE</c>, <c>NULLIF</c>, <c>CASE</c>, <c>CAST</c>, <c>EXTRACT</c>) are
/// not functions: they stay evaluator nodes, as in PostgreSQL's grammar, because a calling
/// convention that evaluates every argument first would change which errors surface (owner decision
/// 66). The evaluator executes these forms directly.
/// </para>
/// </remarks>
internal static class SqlStandardLibrary
{
    internal static bool IsGrouping(string name) => name.Equals("GROUPING", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("GROUPING_ID", StringComparison.OrdinalIgnoreCase);

    /// <summary>The name of <c>COALESCE</c>, the special form the evaluator computes lazily.</summary>
    internal const string Coalesce = "COALESCE";

    /// <summary>The accepted call forms of <c>COALESCE</c>, as a diagnostic shows them.</summary>
    internal const string CoalesceUsage = "COALESCE(value [, value ...])";

    // The built-ins, stateless and shared by every engine. Registration order is the order a
    // diagnostic lists overloads in: COUNT(*) before COUNT(value), as before.
    private static readonly SqlFunction[] _functions =
    [
        new SqlUpperFunction(),
        new SqlLowerFunction(),
        new SqlLengthFunction(),
        new SqlAbsFunction(),
        new SqlBuiltinScalarFunction("CHAR_LENGTH", [SqlType.Text], SqlType.BigInt),
        new SqlBuiltinScalarFunction("CHARACTER_LENGTH", [SqlType.Text], SqlType.BigInt),
        new SqlBuiltinScalarFunction("TRIM", [SqlType.Text], SqlType.Text),
        new SqlBuiltinScalarFunction("TRIM", [SqlType.Text, SqlType.Text], SqlType.Text),
        new SqlBuiltinScalarFunction("LTRIM", [SqlType.Text], SqlType.Text),
        new SqlBuiltinScalarFunction("LTRIM", [SqlType.Text, SqlType.Text], SqlType.Text),
        new SqlBuiltinScalarFunction("RTRIM", [SqlType.Text], SqlType.Text),
        new SqlBuiltinScalarFunction("RTRIM", [SqlType.Text, SqlType.Text], SqlType.Text),
        new SqlBuiltinScalarFunction("SUBSTRING", [SqlType.Text, SqlType.BigInt], SqlType.Text),
        new SqlBuiltinScalarFunction("SUBSTRING", [SqlType.Text, SqlType.BigInt, SqlType.BigInt], SqlType.Text),
        new SqlBuiltinScalarFunction("REPLACE", [SqlType.Text, SqlType.Text, SqlType.Text], SqlType.Text),
        new SqlBuiltinScalarFunction("CONCAT", [SqlType.Any], SqlType.Text, nullBehavior: SqlNullBehavior.CalledOnNullInput, variadic: SqlType.Any),
        new SqlBuiltinScalarFunction("CONCAT_WS", [SqlType.Text, SqlType.Any], SqlType.Text, nullBehavior: SqlNullBehavior.CalledOnNullInput, variadic: SqlType.Any),
        new SqlBuiltinScalarFunction("POSITION", [SqlType.Text, SqlType.Text], SqlType.BigInt),
        new SqlBuiltinScalarFunction("STRPOS", [SqlType.Text, SqlType.Text], SqlType.BigInt),
        new SqlBuiltinScalarFunction("LEFT", [SqlType.Text, SqlType.BigInt], SqlType.Text),
        new SqlBuiltinScalarFunction("RIGHT", [SqlType.Text, SqlType.BigInt], SqlType.Text),
        new SqlBuiltinScalarFunction("LPAD", [SqlType.Text, SqlType.BigInt], SqlType.Text),
        new SqlBuiltinScalarFunction("LPAD", [SqlType.Text, SqlType.BigInt, SqlType.Text], SqlType.Text),
        new SqlBuiltinScalarFunction("RPAD", [SqlType.Text, SqlType.BigInt], SqlType.Text),
        new SqlBuiltinScalarFunction("RPAD", [SqlType.Text, SqlType.BigInt, SqlType.Text], SqlType.Text),
        new SqlBuiltinScalarFunction("REVERSE", [SqlType.Text], SqlType.Text),
        new SqlBuiltinScalarFunction("ROUND", [SqlType.AnyElement], SqlType.AnyElement),
        new SqlBuiltinScalarFunction("ROUND", [SqlType.AnyElement, SqlType.BigInt], SqlType.AnyElement),
        new SqlBuiltinScalarFunction("FLOOR", [SqlType.AnyElement], SqlType.AnyElement),
        new SqlBuiltinScalarFunction("CEILING", [SqlType.AnyElement], SqlType.AnyElement),
        new SqlBuiltinScalarFunction("CEIL", [SqlType.AnyElement], SqlType.AnyElement),
        new SqlBuiltinScalarFunction("POWER", [SqlType.AnyElement, SqlType.AnyNumeric], SqlType.AnyElement),
        new SqlBuiltinScalarFunction("SQRT", [SqlType.AnyNumeric], SqlType.Double),
        new SqlBuiltinScalarFunction("MOD", [SqlType.AnyElement, SqlType.AnyNumeric], SqlType.AnyElement),
        new SqlBuiltinScalarFunction("SIGN", [SqlType.AnyNumeric], SqlType.BigInt),
        new SqlBuiltinScalarFunction("EXP", [SqlType.AnyNumeric], SqlType.Double),
        new SqlBuiltinScalarFunction("LN", [SqlType.AnyNumeric], SqlType.Double),
        new SqlBuiltinScalarFunction("LOG10", [SqlType.AnyNumeric], SqlType.Double),
        new SqlBuiltinScalarFunction("CURRENT_DATE", [], SqlType.Date, SqlFunctionVolatility.Stable),
        new SqlBuiltinScalarFunction("CURRENT_TIME", [], SqlType.Time, SqlFunctionVolatility.Stable),
        new SqlBuiltinScalarFunction("CURRENT_TIMESTAMP", [], SqlType.TimestampTz, SqlFunctionVolatility.Stable),
        new SqlBuiltinScalarFunction("LOCALTIME", [], SqlType.Time, SqlFunctionVolatility.Stable),
        new SqlBuiltinScalarFunction("LOCALTIMESTAMP", [], SqlType.Timestamp, SqlFunctionVolatility.Stable),
        new SqlBuiltinScalarFunction("NOW", [], SqlType.TimestampTz, SqlFunctionVolatility.Stable),
        new SqlBuiltinScalarFunction("DATE_TRUNC", [SqlType.Text, SqlType.AnyElement], SqlType.AnyElement),
        new SqlBuiltinScalarFunction("GEN_RANDOM_UUID", [], SqlType.Uuid, SqlFunctionVolatility.Volatile),
        new SqlCountFunction(rows: true),
        new SqlCountFunction(rows: false),
        new SqlSumFunction(average: false),
        new SqlSumFunction(average: true),
        SqlAggregateFunction.Create<bool, bool, bool>("BOOL_AND", static () => true, static (state, value) => state && value, static state => state),
        SqlAggregateFunction.Create<bool, bool, bool>("EVERY", static () => true, static (state, value) => state && value, static state => state),
        SqlAggregateFunction.Create<bool, bool, bool>("BOOL_OR", static () => false, static (state, value) => state || value, static state => state),
        new SqlStatisticsFunction("STDDEV", sample: true, squareRoot: true),
        new SqlStatisticsFunction("STDDEV", sample: true, squareRoot: true, floating: true),
        new SqlStatisticsFunction("STDDEV_SAMP", sample: true, squareRoot: true),
        new SqlStatisticsFunction("STDDEV_SAMP", sample: true, squareRoot: true, floating: true),
        new SqlStatisticsFunction("STDDEV_POP", sample: false, squareRoot: true),
        new SqlStatisticsFunction("STDDEV_POP", sample: false, squareRoot: true, floating: true),
        new SqlStatisticsFunction("VARIANCE", sample: true, squareRoot: false),
        new SqlStatisticsFunction("VARIANCE", sample: true, squareRoot: false, floating: true),
        new SqlStatisticsFunction("VAR_SAMP", sample: true, squareRoot: false),
        new SqlStatisticsFunction("VAR_SAMP", sample: true, squareRoot: false, floating: true),
        new SqlStatisticsFunction("VAR_POP", sample: false, squareRoot: false),
        new SqlStatisticsFunction("VAR_POP", sample: false, squareRoot: false, floating: true),
        new SqlExtremumFunction(maximum: false),
        new SqlExtremumFunction(maximum: true),
        new SqlStringAggregateFunction("STRING_AGG", delimiter: true),
        new SqlStringAggregateFunction("LISTAGG", delimiter: true),
        new SqlStringAggregateFunction("LISTAGG", delimiter: false),
    ];

    /// <summary>The special forms: grammar, never catalog functions.</summary>
    private static readonly string[] _specialForms = ["COALESCE", "NULLIF", "GROUPING", "GROUPING_ID", "CASE", "CAST", "EXTRACT"];

    /// <summary>
    /// Words the dialect gives a meaning that is not a call of a catalog function: quantifiers, and
    /// the date-time value functions written without parentheses.
    /// </summary>
    private static readonly string[] _reservedWords = ["ANY", "SOME", "LOCALTIME", "LOCALTIMESTAMP"];

    internal static bool IsEvaluatorForm(string name) => IsCoalesce(name) ||
        name.Equals("NULLIF", StringComparison.OrdinalIgnoreCase) || name.Equals("EXTRACT", StringComparison.OrdinalIgnoreCase);

    /// <summary>Registers the built-ins through the public registration path.</summary>
    /// <param name="functions">The collection, empty.</param>
    internal static void Register(SqlFunctionCollection functions)
    {
        foreach (var function in _functions)
        {
            functions.Add(function);
        }
    }

    /// <summary>Whether a function instance is one of the built-ins.</summary>
    /// <param name="function">The function.</param>
    /// <returns><see langword="true"/> for a standard-library leaf.</returns>
    internal static bool Contains(SqlFunction function) => Array.IndexOf(_functions, function) >= 0;

    /// <summary>Whether a name is a built-in's, ignoring case.</summary>
    /// <param name="name">The name as written.</param>
    /// <returns><see langword="true"/> when the standard library registers a function of the name.</returns>
    internal static bool IsStandardName(string name)
    {
        foreach (var function in _functions)
        {
            if (string.Equals(function.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Finds the built-in overload of a function's name that takes some of the same calls: a call
    /// with a number of arguments both accept, or <c>name(*)</c> for two parameterless aggregates.
    /// A built-in is typed over pseudo-types, so any application overload that takes the same calls
    /// would outrank it or tie with it, replacing it for those calls or making them ambiguous, in
    /// stored CHECKs too (owner decision 62: the standard library cannot be replaced).
    /// </summary>
    /// <param name="function">The function being registered.</param>
    /// <returns>The built-in it overlaps, or null.</returns>
    internal static SqlFunction? FindOverlap(SqlFunction function)
    {
        foreach (var builtin in _functions)
        {
            if (string.Equals(builtin.Name, function.Name, StringComparison.OrdinalIgnoreCase) && TakeSameCalls(builtin, function))
            {
                return builtin;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether a built-in overload of a call's name accepts the call's shape: its number of
    /// arguments, or its <c>*</c>. Such a call resolves to a built-in on every engine.
    /// </summary>
    /// <param name="name">The name as written.</param>
    /// <param name="arguments">The call's arguments.</param>
    /// <returns><see langword="true"/> when a standard-library overload takes the call.</returns>
    internal static bool AcceptsShape(string name, IReadOnlyList<SqlExpression> arguments)
    {
        foreach (var builtin in _functions)
        {
            if (string.Equals(builtin.Name, name, StringComparison.OrdinalIgnoreCase) && SqlFunctionResolver.AcceptsShape(builtin, arguments))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The special forms, which are grammar and never catalog functions, including GROUPING and its alias.</summary>
    internal static IReadOnlyList<string> SpecialForms { get; } = Array.AsReadOnly(_specialForms);

    /// <summary>Whether a call names <c>COALESCE</c>.</summary>
    /// <param name="name">The name as written.</param>
    /// <returns><see langword="true"/> for <c>COALESCE</c>, ignoring case.</returns>
    internal static bool IsCoalesce(string name) => string.Equals(name, Coalesce, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the SQL dialect declares an executable function or recognizes an unsupported
    /// spelling (<c>RANK</c>, <c>LEAD</c>, ...). Unsupported calls already carry a
    /// parse-time CK00001; only names in neither list are unknown (#1068, #1103). Read on
    /// the failure path only, for a name the catalog does not hold.
    /// </summary>
    /// <param name="name">The name as written.</param>
    /// <returns><see langword="true"/> when the SQL profile declares the name.</returns>
    internal static bool IsDeclaredName(string name)
    {
        var profile = SqlLanguageProfile.Instance;
        var comparison = profile.IsCaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        foreach (string declared in profile.Functions)
        {
            if (string.Equals(declared, name, comparison))
            {
                return true;
            }
        }

        return SqlLanguageProfile.RecognizedUnsupportedVocabulary.Any(declared => string.Equals(declared, name, comparison));
    }

    /// <summary>
    /// Checks a function name: an identifier (a letter or underscore, then letters, digits and
    /// underscores, as the lexer reads one) that is neither a special form, a SQL keyword, a name
    /// the dialect reserves for a built-in that does not execute yet (<c>RANK</c>, <c>LEAD</c>,
    /// <c>NTILE</c>, ...), a type name (<c>INT</c>, <c>DATE</c>, ...) nor a quantifier or
    /// date-time value function (<c>ANY</c>, <c>SOME</c>, <c>LOCALTIMESTAMP</c>, ...). A reserved
    /// name an application took would fail its build the day the engine ships the built-in, and a
    /// type or quantifier name collides with grammar the dialect will parse.
    /// </summary>
    /// <param name="name">The name.</param>
    /// <param name="sqlDefined">Whether the SQL parser already read the name, including delimiters.</param>
    /// <exception cref="ArgumentException">The name breaks a rule.</exception>
    internal static void ValidateName(string name, bool sqlDefined = false)
    {
        if (!sqlDefined && (name.Length == 0 || !(char.IsLetter(name[0]) || name[0] == '_')))
        {
            throw new ArgumentException(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.StandardLibraryValidateNameArgument, name), nameof(name));
        }
        foreach (char character in name)
        {
            if (!sqlDefined && !(char.IsLetterOrDigit(character) || character == '_'))
            {
                throw new ArgumentException(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.StandardLibraryValidateNameArgument2, name), nameof(name));
            }
        }
        foreach (string form in _specialForms)
        {
            if (string.Equals(form, name, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.StandardLibraryValidateNameArgument3, name), nameof(name));
            }
        }
        foreach (string keyword in SqlLanguageProfile.Instance.Keywords)
        {
            if (string.Equals(keyword, name, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.StandardLibraryValidateNameArgument4, name), nameof(name));
            }
        }
        foreach (string reserved in _reservedWords)
        {
            if (string.Equals(reserved, name, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.StandardLibraryValidateNameArgument5, name), nameof(name));
            }
        }
        if (_functions.Any(function => function is SqlBuiltinScalarFunction && string.Equals(function.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.StandardLibraryValidateNameArgument6, name), nameof(name));
        }
        if (SqlTypeNames.TryResolve(name, out _))
        {
            throw new ArgumentException(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.StandardLibraryValidateNameArgument7, name), nameof(name));
        }
        if (!IsStandardName(name) && IsDeclaredName(name))
        {
            throw new ArgumentException(
                string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.StandardLibraryValidateNameArgument8, name), nameof(name));
        }
    }

    /// <summary>Whether two overloads of one name take some of the same calls.</summary>
    private static bool TakeSameCalls(SqlFunction left, SqlFunction right)
    {
        bool leftStar = IsCalledWithStar(left);
        bool rightStar = IsCalledWithStar(right);
        if (leftStar || rightStar)
        {
            return leftStar && rightStar;
        }

        int leftFixed = left.ParameterTypes.Length;
        int rightFixed = right.ParameterTypes.Length;
        return (left.VariadicParameter is null, right.VariadicParameter is null) switch
        {
            (true, true) => leftFixed == rightFixed,
            (false, true) => rightFixed >= leftFixed,
            (true, false) => leftFixed >= rightFixed,
            _ => true,
        };

        // A parameterless aggregate is called as name(*), and nothing else is.
        static bool IsCalledWithStar(SqlFunction function)
            => function.Kind == SqlFunctionKind.Aggregate && function.ParameterTypes.Length == 0 && function.VariadicParameter is null;
    }
}
