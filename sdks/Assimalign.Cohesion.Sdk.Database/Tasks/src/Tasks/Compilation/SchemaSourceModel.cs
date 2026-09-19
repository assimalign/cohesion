using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Sdk.Database.Tasks.Compilation;

internal sealed record SchemaSourceModel(
    string Name,
    bool AllowsDestructiveChanges,
    IReadOnlyList<SchemaTypeSource> Types,
    IReadOnlyList<SchemaTableSource> Tables,
    IReadOnlyList<SchemaFunctionSource> Functions,
    IReadOnlyList<SchemaTriggerSource> Triggers,
    IReadOnlyList<SchemaPrincipalSource> Principals,
    IReadOnlyList<SchemaExtensionSource> Extensions);

internal sealed record SchemaTypeSource(string TypeName, int? Precision, int? Scale);

internal sealed record SchemaTableSource(
    string Name,
    string RowType,
    IReadOnlyList<SchemaColumnSource> Columns,
    string? PrimaryKey,
    IReadOnlyList<string> Indexes,
    IReadOnlyList<SchemaReferenceSource> References);

internal sealed record SchemaColumnSource(string Name, string TypeName, bool IsNullable);

internal sealed record SchemaReferenceSource(string Member, string TargetType);

internal sealed record SchemaFunctionSource(
    string Name,
    IReadOnlyList<SchemaParameterSource> Parameters,
    string ReturnType,
    string Expression);

internal sealed record SchemaParameterSource(string Name, string TypeName);

internal sealed record SchemaTriggerSource(string RowType, string Event, string Expression);

internal sealed record SchemaPrincipalSource(string Name, IReadOnlyList<SchemaGrantSource> Grants);

internal sealed record SchemaGrantSource(string Permission, IReadOnlyList<string> Objects);

internal sealed record SchemaExtensionSource(string Name, string Value);

internal sealed record SchemaSourceDiagnostic(string Code, string Message, string? File, int Line, int Column);
