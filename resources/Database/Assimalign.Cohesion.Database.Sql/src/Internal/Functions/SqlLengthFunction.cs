using System;
using System.Globalization;
using System.Text;
using System.Threading;

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
            ? CountCharacters(value.AsString().AsSpan().TrimEnd(' '), arguments.Context.CancellationToken)
            : value.Type == DatabaseType.String
            ? CountCharacters(value.AsString(), arguments.Context.CancellationToken)
            : Convert.ToString(value.ToObject(), CultureInfo.InvariantCulture)?.Length ?? 0;
        return SqlValue.FromInt64(length);
    }

    internal static int CountCharacters(ReadOnlySpan<char> text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int count = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if ((++count & 1023) == 0) { cancellationToken.ThrowIfCancellationRequested(); }
        }
        return count;
    }
}
