using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

using Assimalign.Cohesion.Database.Sql.Schema.Internal;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Schema;

/// <summary>Represents an immutable, validated schema for the SQL engine model.</summary>
public sealed class SqlCompiledSchema : CompiledSchema
{
    /// <summary>Gets the current compiled-schema document format.</summary>
    public const string CurrentFormat = "cohesion/database-schema/v1";

    /// <summary>Initializes an immutable compiled schema.</summary>
    /// <param name="format">The document format.</param>
    /// <param name="name">The logical database name.</param>
    /// <param name="model">The owning engine model.</param>
    /// <param name="allowsDestructiveChanges">Whether destructive migrations are permitted.</param>
    /// <param name="types">The custom scalar types.</param>
    /// <param name="tables">The relational tables.</param>
    /// <param name="functions">The database functions.</param>
    /// <param name="triggers">The database triggers.</param>
    /// <param name="principals">The database-scoped principals.</param>
    /// <param name="extensions">The model-specific extensions.</param>
    [JsonConstructor]
    public SqlCompiledSchema(
        string format,
        string name,
        EngineModel model,
        bool allowsDestructiveChanges,
        IReadOnlyList<CompiledSchemaType> types,
        IReadOnlyList<CompiledSchemaTable> tables,
        IReadOnlyList<CompiledSchemaFunction> functions,
        IReadOnlyList<CompiledSchemaTrigger> triggers,
        IReadOnlyList<CompiledSchemaPrincipal> principals,
        IReadOnlyList<CompiledSchemaExtension> extensions)
        : base(format, name, model, allowsDestructiveChanges)
    {
        Types = Snapshot(types, "schema.types", static value => value.Name);
        Tables = Snapshot(tables, "schema.tables", static value => value.Name);
        Functions = Snapshot(functions, "schema.functions", static value => value.Name);
        Triggers = Snapshot(triggers, "schema.triggers", static value => value.Name);
        Principals = Snapshot(principals, "schema.principals", static value => value.Name);
        Extensions = Snapshot(extensions, "schema.extensions", static value => value.Name);

        SqlCompiledSchemaValidator.Validate(this);
    }

    /// <summary>Gets the custom types in stable name order.</summary>
    public IReadOnlyList<CompiledSchemaType> Types { get; }

    /// <summary>Gets the tables in stable name order.</summary>
    public IReadOnlyList<CompiledSchemaTable> Tables { get; }

    /// <summary>Gets the functions in stable name order.</summary>
    public IReadOnlyList<CompiledSchemaFunction> Functions { get; }

    /// <summary>Gets the triggers in stable name order.</summary>
    public IReadOnlyList<CompiledSchemaTrigger> Triggers { get; }

    /// <summary>Gets the database-scoped principals in stable name order.</summary>
    public IReadOnlyList<CompiledSchemaPrincipal> Principals { get; }

    /// <summary>Gets model-specific extension values in stable name order.</summary>
    public IReadOnlyList<CompiledSchemaExtension> Extensions { get; }

    /// <summary>Gets the stable canonical serialization of the SQL schema.</summary>
    [JsonIgnore]
    public override string CanonicalDocument => SqlCompiledSchemaSerializer.Serialize(this);

    private static IReadOnlyList<T> Snapshot<T>(
        IReadOnlyList<T>? values,
        string declaration,
        Func<T, string?> nameSelector)
    {
        if (values is null)
        {
            throw SqlCompiledSchemaValidator.InvalidDocument(
                declaration,
                "The compiled schema collection cannot be null.");
        }

        var copy = new T[values.Count];
        for (int index = 0; index < values.Count; index++)
        {
            if (values[index] is null)
            {
                throw SqlCompiledSchemaValidator.InvalidDocument(
                    $"{declaration}[{index}]",
                    "The compiled schema collection cannot contain a null entry.");
            }

            copy[index] = values[index];
        }

        Array.Sort(copy, (left, right) =>
            StringComparer.Ordinal.Compare(nameSelector(left), nameSelector(right)));
        return Array.AsReadOnly(copy);
    }
}

/// <summary>Describes a compiled custom scalar type.</summary>
/// <param name="Name">The stable custom type name.</param>
/// <param name="StorageType">The underlying storage type.</param>
/// <param name="Precision">The decimal precision.</param>
/// <param name="Scale">The decimal scale.</param>
public sealed record CompiledSchemaType(string Name, DatabaseType StorageType, int? Precision, int? Scale);

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

    private static IReadOnlyList<T> Snapshot<T>(IReadOnlyList<T> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var copy = new T[values.Count];
        for (int index = 0; index < values.Count; index++)
        {
            copy[index] = values[index];
        }

        return Array.AsReadOnly(copy);
    }
}

/// <summary>Describes a compiled SQL table column.</summary>
/// <param name="Name">The column name.</param>
/// <param name="Type">The underlying database type.</param>
/// <param name="IsNullable">Whether null values are permitted.</param>
/// <param name="MaxLength">The optional maximum length.</param>
/// <param name="Precision">The optional decimal precision.</param>
/// <param name="Scale">The optional decimal scale.</param>
/// <param name="CustomType">The custom type name, when one is used.</param>
public sealed record CompiledSchemaColumn(
    string Name,
    DatabaseType Type,
    bool IsNullable,
    int? MaxLength = null,
    int? Precision = null,
    int? Scale = null,
    string? CustomType = null);

/// <summary>Describes a compiled key.</summary>
/// <param name="Name">The stable key name.</param>
/// <param name="Columns">The ordered key columns.</param>
public sealed record CompiledSchemaKey(string Name, IReadOnlyList<string> Columns)
{
    /// <summary>Gets the immutable ordered key columns.</summary>
    public IReadOnlyList<string> Columns { get; } = CompiledSchemaSnapshots.Copy(Columns);
}

/// <summary>Describes a compiled secondary index.</summary>
/// <param name="Name">The stable index name.</param>
/// <param name="Columns">The ordered index columns.</param>
/// <param name="IsUnique">Whether the index enforces uniqueness.</param>
public sealed record CompiledSchemaIndex(string Name, IReadOnlyList<string> Columns, bool IsUnique = false)
{
    /// <summary>
    /// What created this object. Objects from a compiled schema are
    /// <see cref="DatabaseObjectOwner.Schema"/> and are refused to ad-hoc DDL.
    /// Ad-hoc statements may freely change their own ad-hoc objects.
    /// </summary>
    public DatabaseObjectOwner Owner { get; } = DatabaseObjectOwner.Schema;

    /// <summary>Gets the immutable ordered index columns.</summary>
    public IReadOnlyList<string> Columns { get; } = CompiledSchemaSnapshots.Copy(Columns);
}

/// <summary>Identifies a compiled constraint kind.</summary>
public enum CompiledSchemaConstraintKind : byte
{
    /// <summary>A foreign-key reference.</summary>
    Reference = 0,

    /// <summary>A model-specific check constraint.</summary>
    Check,
}

/// <summary>Identifies the action when a referenced parent row is deleted.</summary>
public enum CompiledSchemaReferentialAction : byte
{
    /// <summary>Refuses deletion while dependent rows exist.</summary>
    Restrict = 0,
    /// <summary>Deletes dependent rows in the same transaction.</summary>
    Cascade,
}

/// <summary>Describes a compiled table constraint.</summary>
/// <param name="Name">The stable constraint name.</param>
/// <param name="Kind">The constraint kind.</param>
/// <param name="Columns">The constrained columns.</param>
/// <param name="ReferencedObject">The referenced object, when applicable.</param>
/// <param name="ReferencedColumns">The referenced columns.</param>
/// <param name="Expression">The SQL scalar-expression text for a check, when applicable.</param>
/// <param name="OnDelete">The foreign-key delete action, defaulting to restriction.</param>
public sealed record CompiledSchemaConstraint(
    string Name,
    CompiledSchemaConstraintKind Kind,
    IReadOnlyList<string> Columns,
    string? ReferencedObject,
    IReadOnlyList<string> ReferencedColumns,
    CompiledSchemaExpression? Expression = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    CompiledSchemaReferentialAction OnDelete = CompiledSchemaReferentialAction.Restrict)
{
    /// <summary>
    /// What created this object. Objects from a compiled schema are
    /// <see cref="DatabaseObjectOwner.Schema"/> and are refused to ad-hoc DDL.
    /// Ad-hoc statements may freely change their own ad-hoc objects.
    /// </summary>
    public DatabaseObjectOwner Owner { get; } = DatabaseObjectOwner.Schema;

    /// <summary>Gets the immutable constrained columns.</summary>
    public IReadOnlyList<string> Columns { get; } = CompiledSchemaSnapshots.Copy(Columns);

    /// <summary>Gets the immutable referenced columns.</summary>
    public IReadOnlyList<string> ReferencedColumns { get; } = CompiledSchemaSnapshots.Copy(ReferencedColumns);
}

/// <summary>Describes a compiled function parameter.</summary>
/// <param name="Name">The parameter name.</param>
/// <param name="Type">The storage type.</param>
/// <param name="CustomType">The custom type name, when one is used.</param>
public sealed record CompiledSchemaParameter(string Name, DatabaseType Type, string? CustomType = null);

/// <summary>Describes a compiled function.</summary>
/// <param name="Name">The function name.</param>
/// <param name="Parameters">The ordered parameters.</param>
/// <param name="ResultType">The result storage type.</param>
/// <param name="CustomResultType">The custom result type name.</param>
/// <param name="Body">The canonical function expression.</param>
public sealed record CompiledSchemaFunction(
    string Name,
    IReadOnlyList<CompiledSchemaParameter> Parameters,
    DatabaseType ResultType,
    string? CustomResultType,
    CompiledSchemaExpression Body)
{
    /// <summary>Gets the immutable ordered parameters.</summary>
    public IReadOnlyList<CompiledSchemaParameter> Parameters { get; } = CompiledSchemaSnapshots.Copy(Parameters);
}

/// <summary>Describes a compiled trigger.</summary>
/// <param name="Name">The stable trigger name.</param>
/// <param name="Table">The target table.</param>
/// <param name="Event">The triggering event.</param>
/// <param name="Body">The canonical trigger expression.</param>
public sealed record CompiledSchemaTrigger(
    string Name,
    string Table,
    SqlTriggerEvent Event,
    CompiledSchemaExpression Body);

/// <summary>Describes a compiled database-scoped principal.</summary>
/// <param name="Name">The database-scoped principal name.</param>
/// <param name="Grants">The principal grants.</param>
public sealed record CompiledSchemaPrincipal(string Name, IReadOnlyList<CompiledSchemaGrant> Grants)
{
    /// <summary>Gets the immutable permission grants.</summary>
    public IReadOnlyList<CompiledSchemaGrant> Grants { get; } = CompiledSchemaSnapshots.CopySorted(
        Grants,
        static (left, right) => left.Permission.CompareTo(right.Permission));
}

/// <summary>Describes one compiled permission grant.</summary>
/// <param name="Permission">The granted permission.</param>
/// <param name="Objects">The schema objects receiving the grant.</param>
public sealed record CompiledSchemaGrant(SqlPermission Permission, IReadOnlyList<string> Objects)
{
    /// <summary>Gets the immutable schema object names.</summary>
    public IReadOnlyList<string> Objects { get; } = CompiledSchemaSnapshots.CopySorted(
        Objects,
        static (left, right) => StringComparer.Ordinal.Compare(left, right));
}

/// <summary>Describes a model-specific schema extension.</summary>
/// <param name="Name">The extension name.</param>
/// <param name="Value">The canonical extension value.</param>
public sealed record CompiledSchemaExtension(string Name, string Value);

/// <summary>Contains the portable canonical form of an analyzable expression.</summary>
/// <param name="CanonicalText">The normalized expression AST text.</param>
public sealed record CompiledSchemaExpression(string CanonicalText);
