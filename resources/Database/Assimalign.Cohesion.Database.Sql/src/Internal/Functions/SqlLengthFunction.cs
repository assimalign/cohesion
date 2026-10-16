using System;
using System.Globalization;
using System.Text;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// <c>LENGTH(value)</c>: the number of UTF-16 characters in a value's invariant-culture text, as
/// BIGINT; CHAR counts Unicode scalars after removing trailing ASCII spaces. It accepts any type,
/// as it did before typed signatures (owner decision 67).
/// </summary>
internal sealed class SqlLengthFunction : SqlScalarFunction
{
    /// <summary>Initializes the function.</summary>
    internal SqlLengthFunction()
        : base("LENGTH", [SqlType.Any], SqlType.BigInt, SqlFunctionVolatility.Immutable)
    {
        Usage = "LENGTH(value)";
    }

    /// <inheritdoc />
    protected override SqlValue InvokeCore(scoped in SqlArguments arguments)
    {
        var value = arguments[0];
        int length = value.Type == DatabaseType.Character
            ? CountCharacters(value.AsString().TrimEnd(' '))
            : value.Type == DatabaseType.String
            ? value.AsString().Length
            : Convert.ToString(value.ToObject(), CultureInfo.InvariantCulture)?.Length ?? 0;
        return SqlValue.FromInt64(length);
    }

    private static int CountCharacters(string text)
    {
        int count = 0;
        foreach (var rune in text.EnumerateRunes()) { count++; }
        return count;
    }
}
