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

/// <summary>A <c>schema.Type&lt;T&gt;(t =&gt; t.Decimal(p, s))</c> declaration: a domain of the SQL engine (phase E3).</summary>
/// <param name="TypeName">The CLR type's identity, which a column's member type is matched by.</param>
/// <param name="SqlName">The domain's SQL name: the CLR type's simple name, as the runtime compiler names it.</param>
/// <param name="Precision">The decimal precision.</param>
/// <param name="Scale">The decimal scale.</param>
internal sealed record SchemaTypeSource(string TypeName, string SqlName, int? Precision, int? Scale);

internal sealed record SchemaTableSource(
    string Name,
    string RowType,
    IReadOnlyList<SchemaColumnSource> Columns,
    string? PrimaryKey,
    IReadOnlyList<string> Indexes,
    IReadOnlyList<SchemaReferenceSource> References,
    IReadOnlyList<SchemaCheckSource> Checks);

/// <summary>A column a table declaration selects.</summary>
/// <param name="Name">The member's name.</param>
/// <param name="TypeName">The member's CLR type identity.</param>
/// <param name="IsNullable">Whether the member can hold null.</param>
/// <param name="SqlType">The SQL type <c>table.Column(selector, sqlType)</c> declares, as written; null when none does.</param>
/// <param name="DefaultLiteral">The SQL literal <c>table.Default(selector, literal)</c> declares, as written; null when none does.</param>
internal sealed record SchemaColumnSource(string Name, string TypeName, bool IsNullable, string? SqlType = null, string? DefaultLiteral = null);

internal sealed record SchemaReferenceSource(string Member, string TargetType);

/// <summary>A <c>table.Check(name, sql)</c> declaration, its SQL text as written.</summary>
/// <param name="Name">The constraint name.</param>
/// <param name="Sql">The predicate's SQL text.</param>
internal sealed record SchemaCheckSource(string Name, string Sql);

internal sealed record SchemaPrincipalSource(string Name, IReadOnlyList<SchemaGrantSource> Grants);

internal sealed record SchemaGrantSource(string Permission, IReadOnlyList<string> Objects);

internal sealed record SchemaSourceDiagnostic(string Code, string Message, string? File, int Line, int Column);
