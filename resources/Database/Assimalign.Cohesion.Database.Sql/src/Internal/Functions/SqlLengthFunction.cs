using System;
using System.Globalization;
using System.Text;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// <c>LENGTH(TEXT)</c>: Unicode scalar count as BIGINT; CHAR loses trailing ASCII spaces.
/// </summary>
internal sealed class SqlLengthFunction : SqlScalarFunction
{
    /// <summary>Initializes the function.</summary>
    internal SqlLengthFunction()
        : base("LENGTH", [SqlType.Text], SqlType.BigInt, SqlFunctionVolatility.Immutable)
    {
        Usage = "LENGTH(TEXT)";
    }

    /// <inheritdoc />
    protected override SqlValue InvokeCore(scoped in SqlArguments arguments)
    {
        var value = arguments[0];
        int length = value.Type == DatabaseType.Character
            ? CountCharacters(value.AsString().TrimEnd(' '))
            : value.Type == DatabaseType.String
            ? CountCharacters(value.AsString())
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
