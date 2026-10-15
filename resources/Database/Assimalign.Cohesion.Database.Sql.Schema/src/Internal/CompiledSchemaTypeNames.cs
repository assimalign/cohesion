using System;

using Assimalign.Cohesion.Database.Sql.Language.Internal;

namespace Assimalign.Cohesion.Database.Sql.Schema.Internal;

/// <summary>
/// The shape of a SQL type name a schema can carry: an identifier (a letter or underscore, then
/// letters, digits and underscores), optionally followed by one or two unsigned integer arguments in
/// parentheses (<c>email</c>, <c>VARCHAR(320)</c>, <c>DECIMAL(18, 2)</c>). The SQL engine resolves domain registrations; modifier validation is shared separately.
/// The shape is what makes the name safe to write
/// into DDL text.
/// </summary>
internal static class CompiledSchemaTypeNames
{
    /// <summary>Whether text is an identifier.</summary>
    /// <param name="text">The text.</param>
    /// <returns><see langword="true"/> for a letter or underscore followed by letters, digits and underscores.</returns>
    internal static bool IsIdentifier(ReadOnlySpan<char> text) => SqlTypeModifiers.IsIdentifier(text);

    /// <summary>Whether text is a type name: an identifier, then optionally <c>(n)</c> or <c>(n, m)</c>.</summary>
    /// <param name="text">The text.</param>
    /// <returns><see langword="true"/> when the text has the shape.</returns>
    internal static bool IsTypeName(string text) => SqlTypeModifiers.IsTypeName(text);
}
