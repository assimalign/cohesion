using System;
using System.Globalization;

using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// The rules a domain's name follows, and the reading of a type name as DDL and CAST write it
/// (<c>VARCHAR(100)</c>, <c>DECIMAL(18, 4)</c>, <c>email</c>), shared by the engine's type
/// catalog, the planner and provisioning.
/// </summary>
internal static class SqlTypeRules
{
    // The pseudo-types' names: a function's parameter types, never a column's type.
    private static readonly string[] _pseudoNames = ["ANY", "ANYELEMENT", "ANYNUMERIC"];

    // Words the dialect gives a meaning that is not a type: quantifiers and date-time value words.
    private static readonly string[] _reservedWords = ["SOME", "LOCALTIME", "LOCALTIMESTAMP"];

    /// <summary>
    /// Checks a domain's name: an identifier (a letter or underscore, then letters, digits and
    /// underscores, as the lexer reads one) that is neither a SQL keyword, a quantifier or date-time
    /// value word, a pseudo-type's name, nor a built-in type name or alias. A domain named like a
    /// built-in type would be unreachable, since built-in names resolve first.
    /// </summary>
    /// <param name="name">The name.</param>
    /// <exception cref="ArgumentException">The name breaks a rule.</exception>
    internal static void ValidateDomainName(string name)
    {
        if (name.Length == 0 || !(char.IsLetter(name[0]) || name[0] == '_'))
        {
            throw new ArgumentException($"Domain name '{name}' is not an identifier: it must start with a letter or an underscore.", nameof(name));
        }
        foreach (char character in name)
        {
            if (!(char.IsLetterOrDigit(character) || character == '_'))
            {
                throw new ArgumentException($"Domain name '{name}' is not an identifier: it may hold only letters, digits and underscores.", nameof(name));
            }
        }
        foreach (string keyword in SqlLanguageProfile.Instance.Keywords)
        {
            if (string.Equals(keyword, name, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"'{name}' is a SQL keyword and cannot name a domain.", nameof(name));
            }
        }
        foreach (string reserved in _reservedWords)
        {
            if (string.Equals(reserved, name, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"'{name}' is reserved by the SQL dialect and cannot name a domain.", nameof(name));
            }
        }
        foreach (string pseudo in _pseudoNames)
        {
            if (string.Equals(pseudo, name, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"'{name}' is a pseudo-type's name and cannot name a domain.", nameof(name));
            }
        }
        if (SqlTypeNames.TryResolve(name, out _))
        {
            throw new ArgumentException($"'{name}' is a built-in SQL type name and cannot name a domain.", nameof(name));
        }
    }

    /// <summary>
    /// Splits a type as written into its name and up to two unsigned integer arguments:
    /// <c>VARCHAR(100)</c>, <c>DECIMAL(18, 4)</c>, <c>email</c>. A name the lexer kept quoted
    /// (<c>"email"</c>) is read without its quotes.
    /// </summary>
    /// <param name="text">The type as written.</param>
    /// <param name="name">The name.</param>
    /// <param name="first">The first argument, when one was written.</param>
    /// <param name="second">The second argument, when one was written.</param>
    /// <returns><see langword="false"/> when the arguments are not one or two unsigned integers.</returns>
    internal static bool TrySplit(string text, out string name, out int? first, out int? second)
    {
        name = text.Trim();
        first = null;
        second = null;
        int open = name.IndexOf('(');
        if (open >= 0)
        {
            string arguments = name[(open + 1)..];
            name = name[..open].TrimEnd();
            if (!arguments.EndsWith(')'))
            {
                return false;
            }

            var parts = arguments[..^1].Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length > 2 ||
                !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int parsedFirst))
            {
                return false;
            }

            first = parsedFirst;
            if (parts.Length == 2)
            {
                if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int parsedSecond))
                {
                    return false;
                }

                second = parsedSecond;
            }
        }

        if (name.Length >= 2 && name[0] == '"' && name[^1] == '"')
        {
            name = name[1..^1];
        }

        return name.Length > 0;
    }

    /// <summary>
    /// Resolves a built-in type name and its arguments as DDL does: a second argument is always a
    /// scale (<c>DECIMAL(p, s)</c>), and <see cref="SqlTypeNames"/> moves a single argument from
    /// length to precision for the decimal kinds.
    /// </summary>
    /// <param name="name">The name, without arguments.</param>
    /// <param name="first">The first argument.</param>
    /// <param name="second">The second argument.</param>
    /// <param name="storage">The storage type and its constraints.</param>
    /// <returns><see langword="true"/> when the name is a built-in type's.</returns>
    internal static bool TryResolveBuiltIn(string name, int? first, int? second, out DatabaseTypeInfo storage)
        => SqlTypeNames.TryResolve(name, first, second is null ? null : first, second, out storage);
}
