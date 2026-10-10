using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

using Assimalign.Cohesion.Database.Sql.Schema.Internal;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Schema;

/// <summary>Represents an immutable, validated schema for the SQL engine model.</summary>
/// <remarks>
/// <para>
/// <b>Standalone</b> (owner decisions 50 and 59 of 2026-10-09). It used to derive the area root's
/// model-agnostic <c>CompiledSchema</c>, which carried an engine model and recomputed the
/// canonical document and its hash on every read. The root keeps no schema type now: provisioning
/// belongs to the SQL model, which owns this shape, its canonical document and its migration
/// planner. Dropping the inherited <c>Model</c> changed the document, so its format is
/// <c>cohesion/database-schema/v2</c>; a <c>v1</c> document no longer deserializes.
/// </para>
/// <para>
/// <see cref="CanonicalDocument"/> and <see cref="Hash"/> are computed once, the first time either
/// is read, and kept: the schema is immutable, so they never change.
/// </para>
/// </remarks>
public sealed class SqlCompiledSchema
{
    /// <summary>Gets the current compiled-schema document format.</summary>
    public const string CurrentFormat = "cohesion/database-schema/v2";

    // Computed once, on first read: the schema is immutable. Two threads that race the first read
    // compute the same strings, so the race costs only the duplicate work.
    private string? _canonicalDocument;
    private string? _hash;

    /// <summary>Initializes an immutable compiled schema.</summary>
    /// <param name="format">The document format.</param>
    /// <param name="name">The logical database name.</param>
    /// <param name="allowsDestructiveChanges">Whether destructive migrations are permitted.</param>
    /// <param name="types">The custom scalar types.</param>
    /// <param name="tables">The relational tables.</param>
    /// <param name="principals">The database-scoped principals.</param>
    /// <exception cref="SqlSchemaValidationException">The values do not satisfy the compiled-schema contract.</exception>
    [JsonConstructor]
    public SqlCompiledSchema(
        string format,
        string name,
        bool allowsDestructiveChanges,
        IReadOnlyList<CompiledSchemaType> types,
        IReadOnlyList<CompiledSchemaTable> tables,
        IReadOnlyList<CompiledSchemaPrincipal> principals)
    {
        Format = format;
        Name = name;
        AllowsDestructiveChanges = allowsDestructiveChanges;
        Types = Snapshot(types, "schema.types", static value => value.Name);
        Tables = Snapshot(tables, "schema.tables", static value => value.Name);
        Principals = Snapshot(principals, "schema.principals", static value => value.Name);

        SqlCompiledSchemaValidator.Validate(this);
    }

    /// <summary>Gets the schema document format identifier.</summary>
    public string Format { get; }

    /// <summary>Gets the name of the logical database this schema targets.</summary>
    public string Name { get; }

    /// <summary>Gets whether applying this schema may perform destructive operations.</summary>
    public bool AllowsDestructiveChanges { get; }

    /// <summary>Gets the custom types in stable name order.</summary>
    public IReadOnlyList<CompiledSchemaType> Types { get; }

    /// <summary>Gets the tables in stable name order.</summary>
    public IReadOnlyList<CompiledSchemaTable> Tables { get; }

    /// <summary>Gets the database-scoped principals in stable name order.</summary>
    public IReadOnlyList<CompiledSchemaPrincipal> Principals { get; }

    /// <summary>
    /// Gets the stable canonical serialization of the SQL schema. Two schemas with the same
    /// canonical document are the same schema; it is what <see cref="Hash"/> is computed over and
    /// what the catalog records for drift detection. Computed once, on first read.
    /// </summary>
    [JsonIgnore]
    public string CanonicalDocument => _canonicalDocument ??= SqlCompiledSchemaSerializer.Serialize(this);

    /// <summary>
    /// Gets the lowercase hexadecimal SHA-256 of the UTF-8 <see cref="CanonicalDocument"/>.
    /// Computed once, on first read.
    /// </summary>
    [JsonIgnore]
    public string Hash => _hash ??= Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalDocument)));

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

/// <summary>Contains the portable canonical form of an analyzable expression.</summary>
/// <param name="CanonicalText">The normalized expression AST text.</param>
public sealed record CompiledSchemaExpression(string CanonicalText);
