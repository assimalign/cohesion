using System.Text.Json.Serialization;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Schema;

/// <summary>Describes a compiled SQL table column.</summary>
/// <param name="Name">The column name.</param>
/// <param name="Type">The underlying database type.</param>
/// <param name="IsNullable">Whether null values are permitted.</param>
/// <param name="MaxLength">The optional maximum length.</param>
/// <param name="Precision">The optional decimal precision.</param>
/// <param name="Scale">The optional decimal scale; a compiled table normalizes an omitted scale to zero when precision is given.</param>
/// <param name="CustomType">
/// The SQL type the column is declared with, when it does not follow from the member's CLR type: a
/// domain the schema declares, a domain the SQL engine registers, or a built-in type name with its
/// arguments, as written (phase E3). The engine resolves it as its DDL does.
/// </param>
/// <param name="DefaultLiteral">
/// The rendered typed default or raw <c>DefaultSql</c> literal as written, including SQL quotes
/// for a string, or null when no default is declared. The SQL engine validates and canonicalizes
/// the literal before applying it.
/// </param>
public sealed record CompiledSchemaColumn(
    string Name,
    DatabaseType Type,
    bool IsNullable,
    int? MaxLength = null,
    int? Precision = null,
    int? Scale = null,
    string? CustomType = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? DefaultLiteral = null);
