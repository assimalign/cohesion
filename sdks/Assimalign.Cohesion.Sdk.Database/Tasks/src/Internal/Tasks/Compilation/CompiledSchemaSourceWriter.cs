using System;
using System.Collections.Generic;
using System.Linq;

using Assimalign.Cohesion.Database;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Sdk.Database.Tasks.Internal;

/// <summary>Maps Roslyn-lowered declarations onto the SQL schema package's canonical contract.</summary>
internal static class CompiledSchemaSourceWriter
{
    public static SqlCompiledSchema Create(SchemaSourceModel source, string modelName)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!string.Equals(modelName, "Sql", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Database model '{modelName}' has no model-specific compiled-schema package.");
        }

        var customTypes = source.Types.ToDictionary(
            static type => type.TypeName,
            static type => new CompiledSchemaType(type.TypeName, DatabaseType.Decimal, type.Precision, type.Scale),
            StringComparer.Ordinal);
        CompiledSchemaColumn CreateColumn(SchemaColumnSource column)
        {
            (DatabaseType type, string? custom, int? precision, int? scale) = ResolveType(column.TypeName, customTypes);
            return new CompiledSchemaColumn(
                column.Name,
                type,
                column.IsNullable,
                Precision: precision,
                Scale: scale,
                CustomType: custom);
        }

        var tablesByRowType = source.Tables.ToDictionary(static table => table.RowType, StringComparer.Ordinal);
        var tables = new List<CompiledSchemaTable>();
        foreach (SchemaTableSource table in source.Tables)
        {
            CompiledSchemaKey? primaryKey = table.PrimaryKey is null
                ? null
                : new CompiledSchemaKey($"PK_{table.Name}", Array.AsReadOnly([table.PrimaryKey]));
            CompiledSchemaIndex[] indexes = table.Indexes
                .OrderBy(static member => member, StringComparer.Ordinal)
                .Select(member => new CompiledSchemaIndex($"IX_{table.Name}_{member}", Array.AsReadOnly([member])))
                .ToArray();
            CompiledSchemaConstraint[] constraints = table.References
                .OrderBy(static reference => reference.Member, StringComparer.Ordinal)
                .Select(reference =>
                {
                    SchemaTableSource target = tablesByRowType[reference.TargetType];
                    return new CompiledSchemaConstraint(
                        $"FK_{table.Name}_{target.Name}_{reference.Member}",
                        CompiledSchemaConstraintKind.Reference,
                        Array.AsReadOnly([reference.Member]),
                        target.Name,
                        Array.AsReadOnly([target.PrimaryKey!]));
                })
                .ToArray();
            tables.Add(new CompiledSchemaTable(
                table.Name,
                table.RowType,
                table.Columns.Select(CreateColumn).ToArray(),
                primaryKey,
                indexes,
                constraints));
        }

        CompiledSchemaFunction[] functions = source.Functions
            .Select(function =>
            {
                (DatabaseType resultType, string? customResult, _, _) = ResolveType(function.ReturnType, customTypes);
                CompiledSchemaParameter[] parameters = function.Parameters
                    .Select(parameter =>
                    {
                        (DatabaseType type, string? custom, _, _) = ResolveType(parameter.TypeName, customTypes);
                        return new CompiledSchemaParameter(parameter.Name, type, custom);
                    })
                    .ToArray();
                return new CompiledSchemaFunction(
                    function.Name,
                    parameters,
                    resultType,
                    customResult,
                    new CompiledSchemaExpression(function.Expression));
            })
            .ToArray();

        CompiledSchemaTrigger[] triggers = source.Triggers
            .Select(trigger =>
            {
                SchemaTableSource table = tablesByRowType[trigger.RowType];
                SqlTriggerEvent triggerEvent = Enum.Parse<SqlTriggerEvent>(trigger.Event, ignoreCase: false);
                return new CompiledSchemaTrigger(
                    $"TR_{table.Name}_{triggerEvent}",
                    table.Name,
                    triggerEvent,
                    new CompiledSchemaExpression(trigger.Expression));
            })
            .ToArray();

        CompiledSchemaPrincipal[] principals = source.Principals
            .Select(principal => new CompiledSchemaPrincipal(
                principal.Name,
                principal.Grants
                    .GroupBy(static grant => grant.Permission, StringComparer.Ordinal)
                    .OrderBy(group => Enum.Parse<SqlPermission>(group.Key, ignoreCase: false))
                    .Select(group => new CompiledSchemaGrant(
                        Enum.Parse<SqlPermission>(group.Key, ignoreCase: false),
                        group.SelectMany(static grant => grant.Objects)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderBy(static item => item, StringComparer.Ordinal)
                            .ToArray()))
                    .ToArray()))
            .ToArray();

        return new SqlCompiledSchema(
            SqlCompiledSchema.CurrentFormat,
            source.Name,
            EngineModel.Sql,
            source.AllowsDestructiveChanges,
            customTypes.Values.OrderBy(static type => type.Name, StringComparer.Ordinal).ToArray(),
            tables.OrderBy(static table => table.Name, StringComparer.Ordinal).ToArray(),
            functions.OrderBy(static function => function.Name, StringComparer.Ordinal).ToArray(),
            triggers.OrderBy(static trigger => trigger.Name, StringComparer.Ordinal).ToArray(),
            principals.OrderBy(static principal => principal.Name, StringComparer.Ordinal).ToArray(),
            source.Extensions
                .OrderBy(static extension => extension.Name, StringComparer.Ordinal)
                .Select(static extension => new CompiledSchemaExtension(extension.Name, extension.Value))
                .ToArray());
    }

    private static (DatabaseType Type, string? CustomType, int? Precision, int? Scale) ResolveType(
        string typeName,
        IReadOnlyDictionary<string, CompiledSchemaType> customTypes)
    {
        if (customTypes.TryGetValue(typeName, out CompiledSchemaType? custom))
        {
            return (custom.StorageType, custom.Name, custom.Precision, custom.Scale);
        }

        DatabaseType type = typeName switch
        {
            "System.Private.CoreLib:System.Boolean" => DatabaseType.Boolean,
            "System.Private.CoreLib:System.SByte" => DatabaseType.Int8,
            "System.Private.CoreLib:System.Byte" or "System.Private.CoreLib:System.Int16" => DatabaseType.Int16,
            "System.Private.CoreLib:System.Int32" => DatabaseType.Int32,
            "System.Private.CoreLib:System.Int64" => DatabaseType.Int64,
            "System.Private.CoreLib:System.Single" => DatabaseType.Float32,
            "System.Private.CoreLib:System.Double" => DatabaseType.Float64,
            "System.Private.CoreLib:System.Decimal" => DatabaseType.Decimal,
            "System.Private.CoreLib:System.String" => DatabaseType.String,
            "System.Private.CoreLib:System.Byte[]" => DatabaseType.Binary,
            "System.Private.CoreLib:System.DateOnly" => DatabaseType.Date,
            "System.Private.CoreLib:System.TimeOnly" => DatabaseType.Time,
            "System.Private.CoreLib:System.DateTime" => DatabaseType.DateTime,
            "System.Private.CoreLib:System.DateTimeOffset" => DatabaseType.DateTimeOffset,
            "System.Private.CoreLib:System.TimeSpan" => DatabaseType.TimeSpan,
            "System.Private.CoreLib:System.Guid" => DatabaseType.Guid,
            _ => throw new InvalidOperationException($"CLR type '{typeName}' is not supported by the compiled-schema contract.")
        };
        return (type, null, null, null);
    }
}
