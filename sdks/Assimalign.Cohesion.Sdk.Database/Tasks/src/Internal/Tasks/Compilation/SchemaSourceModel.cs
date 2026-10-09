using System.Collections.Generic;

namespace Assimalign.Cohesion.Sdk.Database.Tasks.Internal;

/// <summary>One database's schema declaration, as the extractor read it from consumer C#.</summary>
/// <param name="Name">The database name, which is also the compiled schema's name.</param>
/// <param name="AllowsDestructiveChanges">Whether the declaration allows destructive changes.</param>
/// <param name="Types">The custom scalar types.</param>
/// <param name="Tables">The tables.</param>
/// <param name="Principals">The database-scoped principals.</param>
internal sealed record SchemaSourceModel(
    string Name,
    bool AllowsDestructiveChanges,
    IReadOnlyList<SchemaTypeSource> Types,
    IReadOnlyList<SchemaTableSource> Tables,
    IReadOnlyList<SchemaPrincipalSource> Principals);

internal sealed record SchemaTypeSource(string TypeName, int? Precision, int? Scale);

internal sealed record SchemaTableSource(
    string Name,
    string RowType,
    IReadOnlyList<SchemaColumnSource> Columns,
    string? PrimaryKey,
    IReadOnlyList<string> Indexes,
    IReadOnlyList<SchemaReferenceSource> References,
    IReadOnlyList<SchemaCheckSource> Checks);

internal sealed record SchemaColumnSource(string Name, string TypeName, bool IsNullable);

internal sealed record SchemaReferenceSource(string Member, string TargetType);

/// <summary>A <c>table.Check(name, sql)</c> declaration, its SQL text as written.</summary>
/// <param name="Name">The constraint name.</param>
/// <param name="Sql">The predicate's SQL text.</param>
internal sealed record SchemaCheckSource(string Name, string Sql);

internal sealed record SchemaPrincipalSource(string Name, IReadOnlyList<SchemaGrantSource> Grants);

internal sealed record SchemaGrantSource(string Permission, IReadOnlyList<string> Objects);

internal sealed record SchemaSourceDiagnostic(string Code, string Message, string? File, int Line, int Column);
