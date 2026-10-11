using System;

namespace Assimalign.Cohesion.Database.Sql.Schema.Internal;

/// <summary>
/// The shape of a SQL type name a schema can carry: an identifier (a letter or underscore, then
/// letters, digits and underscores), optionally followed by one or two unsigned integer arguments in
/// parentheses (<c>email</c>, <c>VARCHAR(320)</c>, <c>DECIMAL(18, 2)</c>). This package does not know
/// which names exist: the SQL engine resolves them. The shape is what makes the name safe to write
/// into DDL text.
/// </summary>
internal static class CompiledSchemaTypeNames
{
    /// <summary>Whether text is an identifier.</summary>
    /// <param name="text">The text.</param>
    /// <returns><see langword="true"/> for a letter or underscore followed by letters, digits and underscores.</returns>
    internal static bool IsIdentifier(ReadOnlySpan<char> text)
    {
        if (text.Length == 0 || !(char.IsAsciiLetter(text[0]) || text[0] == '_'))
        {
            return false;
        }

        foreach (char character in text)
        {
            if (!(char.IsAsciiLetterOrDigit(character) || character == '_'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether text is a type name: an identifier, then optionally <c>(n)</c> or <c>(n, m)</c>.</summary>
    /// <param name="text">The text.</param>
    /// <returns><see langword="true"/> when the text has the shape.</returns>
    internal static bool IsTypeName(string text)
    {
        int open = text.IndexOf('(');
        if (open < 0)
        {
            return IsIdentifier(text);
        }

        if (!IsIdentifier(text.AsSpan(0, open).TrimEnd()) || !text.EndsWith(')'))
        {
            return false;
        }

        var arguments = text.AsSpan(open + 1, text.Length - open - 2);
        int comma = arguments.IndexOf(',');
        return comma < 0
            ? IsUnsigned(arguments)
            : IsUnsigned(arguments[..comma]) && IsUnsigned(arguments[(comma + 1)..]);

        static bool IsUnsigned(ReadOnlySpan<char> argument)
        {
            argument = argument.Trim();
            if (argument.Length == 0 || argument.Length > 9)
            {
                return false;
            }

            foreach (char character in argument)
            {
                if (!char.IsAsciiDigit(character))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
