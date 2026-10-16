using System;

using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Sql.Language.Internal;
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
    /// Checks a domain's name: an ASCII identifier (an ASCII letter or underscore, then ASCII letters,
    /// digits and underscores) that is neither a SQL keyword, a quantifier or date-time value word, a
    /// pseudo-type's name, nor a built-in type name or alias. A domain named like a built-in type
    /// would be unreachable, since built-in names resolve first. ASCII is the rule the typed schema,
    /// <c>Sdk.Database</c> and the DDL renderer apply to a type name, so every domain the engine
    /// registers can be named in <c>table.Column(selector, sqlType)</c>, and the case-insensitive
    /// matches the catalog makes agree with its upper-cased cast keys.
    /// </summary>
    /// <param name="name">The name.</param>
    /// <exception cref="ArgumentException">The name breaks a rule.</exception>
    internal static void ValidateDomainName(string name)
    {
        if (name.Length == 0 || !(char.IsAsciiLetter(name[0]) || name[0] == '_'))
        {
            throw new ArgumentException($"Domain name '{name}' is not an identifier: it must start with an ASCII letter or an underscore.", nameof(name));
        }
        foreach (char character in name)
        {
            if (!(char.IsAsciiLetterOrDigit(character) || character == '_'))
            {
                throw new ArgumentException(
                    $"Domain name '{name}' is not an identifier: it may hold only ASCII letters, digits and underscores.", nameof(name));
            }
        }
        ValidateSqlDomainName(name);
    }

    // SQL has already lexed a delimited name, so punctuation is part of its value.
    // Native registrations keep the ASCII name contract the typed schema consumes.
    internal static void ValidateSqlDomainName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
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
        => SqlTypeModifiers.TrySplit(text, out name, out first, out second);

    /// <summary>
    /// Whether two type names as written name the same type the way the catalog resolves them: the
    /// same name ignoring case and quotes (<c>email</c>, <c>"Email"</c>), with the same arguments. Text
    /// that does not split compares as written.
    /// </summary>
    /// <param name="left">One type as written.</param>
    /// <param name="right">The other type as written.</param>
    /// <returns><see langword="true"/> when both name the same type.</returns>
    internal static bool SameTypeName(string left, string right)
        => TrySplit(left, out string leftName, out int? leftFirst, out int? leftSecond)
            && TrySplit(right, out string rightName, out int? rightFirst, out int? rightSecond)
                ? string.Equals(leftName, rightName, StringComparison.OrdinalIgnoreCase) && leftFirst == rightFirst && leftSecond == rightSecond
                : string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

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
        => SqlTypeNames.TryResolve(name, first, null, second, out storage);
}
