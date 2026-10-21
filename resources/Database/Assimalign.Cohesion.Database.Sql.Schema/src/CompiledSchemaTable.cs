using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

using Assimalign.Cohesion.Database.Sql.Language.Internal;
using Assimalign.Cohesion.Database.Sql.Schema.Internal;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Schema;

/// <summary>Describes a compiled table.</summary>
public sealed class CompiledSchemaTable
{
    /// <summary>Initializes a compiled table.</summary>
    /// <param name="name">The stable table name.</param>
    /// <param name="rowType">The stable CLR row type identifier.</param>
    /// <param name="columns">The ordered columns.</param>
    /// <param name="primaryKey">The primary key.</param>
    /// <param name="indexes">The secondary indexes.</param>
    /// <param name="constraints">The table constraints.</param>
    [JsonConstructor]
    public CompiledSchemaTable(
        string name,
        string rowType,
        IReadOnlyList<CompiledSchemaColumn> columns,
        CompiledSchemaKey? primaryKey,
        IReadOnlyList<CompiledSchemaIndex> indexes,
        IReadOnlyList<CompiledSchemaConstraint> constraints)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(rowType);
        Name = name;
        RowType = rowType;
        Columns = Snapshot(columns);
        PrimaryKey = primaryKey;
        Indexes = CompiledSchemaSnapshots.CopySorted(
            indexes,
            static (left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
        Constraints = CompiledSchemaSnapshots.CopySorted(
            constraints,
            static (left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
    }

    /// <summary>
    /// What created this object. Objects from a compiled schema are
    /// <see cref="DatabaseObjectOwner.Schema"/> and are refused to ad-hoc DDL.
    /// Ad-hoc statements may freely change their own ad-hoc objects.
    /// </summary>
    public DatabaseObjectOwner Owner { get; } = DatabaseObjectOwner.Schema;

    /// <summary>Gets the stable table name.</summary>
    public string Name { get; }

    /// <summary>Gets the stable CLR row type identifier.</summary>
    public string RowType { get; }

    /// <summary>Gets the ordered columns.</summary>
    public IReadOnlyList<CompiledSchemaColumn> Columns { get; }

    /// <summary>Gets the primary key.</summary>
    public CompiledSchemaKey? PrimaryKey { get; }

    /// <summary>Gets the secondary indexes in stable name order.</summary>
    public IReadOnlyList<CompiledSchemaIndex> Indexes { get; }

    /// <summary>Gets the constraints in stable name order.</summary>
    public IReadOnlyList<CompiledSchemaConstraint> Constraints { get; }

    private static IReadOnlyList<CompiledSchemaColumn> Snapshot(IReadOnlyList<CompiledSchemaColumn> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var copy = new CompiledSchemaColumn[values.Count];
        for (int index = 0; index < values.Count; index++)
        {
            var column = values[index];
            if (column is not null)
            {
                if (column.Type is DatabaseType.String or DatabaseType.Character && column.CustomType is { } name
                    && SqlTypeModifiers.TrySplit(name, out string typeName, out int? length, out _)
                    && SqlTypeModifiers.StorageType(typeName) is DatabaseType.Character)
                {
                    column = column with { Type = DatabaseType.Character, MaxLength = length ?? 1 };
                }
                else if (column.Type == DatabaseType.Character && column.CustomType is null)
                {
                    column = column with { MaxLength = column.MaxLength ?? 1 };
                }
            }
            copy[index] = column is null ? null! : column with
            {
                Scale = SqlTypeModifiers.NormalizeScale(column.Type, column.Precision, column.Scale),
            };
        }

        return Array.AsReadOnly(copy);
    }
}
