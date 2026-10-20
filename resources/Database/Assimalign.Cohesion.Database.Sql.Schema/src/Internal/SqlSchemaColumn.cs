using System;

namespace Assimalign.Cohesion.Database.Sql.Schema.Internal;

/// <summary>A typed column selected from a schema row type.</summary>
/// <param name="Name">The column name.</param>
/// <param name="ClrType">The declared CLR value type.</param>
/// <param name="IsNullable">Whether null values are permitted.</param>
/// <param name="SqlType">
/// The SQL type the column is declared with (<c>table.Column(selector, sqlType)</c>): a domain's name,
/// or a built-in type name with its arguments; null when the type follows from the CLR type.
/// </param>
/// <param name="DefaultLiteral">The rendered typed default or raw DefaultSql literal, or null when no default is declared.</param>
internal sealed record SqlSchemaColumn(string Name, Type ClrType, bool IsNullable, string? SqlType = null,
    string? DefaultLiteral = null);
