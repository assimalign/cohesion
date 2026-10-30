using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;

using Assimalign.Cohesion.Database.Sql.Language.Internal;
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
/// planner. Dropping the inherited <c>Model</c> changed the document, so its format became
/// <c>cohesion/database-schema/v2</c>; a <c>v1</c> document no longer deserializes.
/// </para>
/// <para>
/// <b>Format <c>v3</c> (phase E3).</b> A declared type (<c>schema.Type&lt;T&gt;</c>) is a domain of the
/// SQL engine, so <see cref="CompiledSchemaType.Name"/> is its SQL name, the CLR type's simple name,
/// where <c>v2</c> held the CLR type's identity; and a column may be declared with a SQL type
/// (<c>table.Column(selector, sqlType)</c>), kept in <see cref="CompiledSchemaColumn.CustomType"/> as
/// written. Earlier document formats are refused.
/// </para>
/// <para>
/// <b>Format <c>v4</c> (phase B2).</b> Columns may declare
/// <see cref="CompiledSchemaColumn.DefaultLiteral"/>. Strict earlier readers cannot read that
/// member, so B2 advanced the format even for schemas without defaults.
/// </para>
/// <para>
/// <b>Format <c>v5</c> (#1438).</b> Decimal precision with an omitted scale is normalized to zero
/// before hashing. Earlier documents, including persisted applied markers and pending targets,
/// are refused through the format gate. Rebuild artifacts and recreate affected databases;
/// there is no upgrade path or reconciliation of earlier identities. The SDK uses this format too.
/// </para>
/// <para>
/// <b>Format <c>v6</c> (#1437).</b> Fixed-length character columns have the distinct
/// <see cref="DatabaseType.Character"/> identity and a declared length; bare CHAR means one.
/// Runtime and SDK documents share this format and reject earlier documents.
/// </para>
/// <para>
/// <b>Format <c>v9</c> (#1723).</b> Foreign-key constraints carry explicit ON UPDATE metadata,
/// defaulting to NO ACTION. Runtime and SDK reject earlier artifacts, including v8.
/// </para>
/// <para>
/// <b>Format <c>v10</c> (#1630).</b> Indexes may retain optional SQL predicate text.
/// Runtime and SDK reject every earlier artifact, including v9.
/// </para>
/// <para>
/// <see cref="CanonicalDocument"/> and <see cref="Hash"/> are computed once, the first time either
/// is read, and kept: the schema is immutable, so they never change.
/// </para>
/// </remarks>
[global::System.Diagnostics.DebuggerDisplay("SqlCompiledSchema {Name,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Schema.SqlCompiledSchema.DebugView))]
public sealed class SqlCompiledSchema
{
    /// <summary>Gets the current compiled-schema document format.</summary>
    public const string CurrentFormat = "cohesion/database-schema/v10";

    // Computed once, on first read: the schema is immutable. Two threads that race the first read
    // compute the same strings, so the race costs only the duplicate work.
    private string? _canonicalDocument;
    private string? _hash;
    private int _serializationComputationCount;
    private int _hashComputationCount;

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
        SqlCompiledSchemaValidator.ValidateFormat(format);
        Format = format;
        Name = name;
        AllowsDestructiveChanges = allowsDestructiveChanges;
        Types = Snapshot(types, "schema.types", static value => value.Name,
            static value => value with { Scale = SqlTypeModifiers.NormalizeScale(value.StorageType, value.Precision, value.Scale) });
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
    public string CanonicalDocument => _canonicalDocument ??= ComputeCanonicalDocument();

    /// <summary>
    /// Gets the lowercase hexadecimal SHA-256 of the UTF-8 <see cref="CanonicalDocument"/>.
    /// Computed once, on first read.
    /// </summary>
    [JsonIgnore]
    public string Hash => _hash ??= ComputeHash();

    // Owning-assembly test hooks: reading a warmed identity must do neither operation again.
    internal int SerializationComputationCount => Volatile.Read(ref _serializationComputationCount);
    internal int HashComputationCount => Volatile.Read(ref _hashComputationCount);

    private string ComputeCanonicalDocument()
    {
        Interlocked.Increment(ref _serializationComputationCount);
        return SqlCompiledSchemaSerializer.Serialize(this);
    }

    private string ComputeHash()
    {
        Interlocked.Increment(ref _hashComputationCount);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalDocument)));
    }

    private static IReadOnlyList<T> Snapshot<T>(
        IReadOnlyList<T>? values,
        string declaration,
        Func<T, string?> nameSelector,
        Func<T, T>? normalize = null)
    {
        if (values is null)
        {
            throw SqlCompiledSchemaValidator.InvalidDocument(
                declaration,
                ErrorMessages.SqlCompiledSchemaSnapshotFailure);
        }

        var copy = new T[values.Count];
        for (int index = 0; index < values.Count; index++)
        {
            if (values[index] is null)
            {
                throw SqlCompiledSchemaValidator.InvalidDocument(
                    $"{declaration}[{index}]",
                    ErrorMessages.SqlCompiledSchemaSnapshotFailure2);
            }

            copy[index] = normalize is null ? values[index] : normalize(values[index]);
        }

        Array.Sort(copy, (left, right) =>
            StringComparer.Ordinal.Compare(nameSelector(left), nameSelector(right)));
        return Array.AsReadOnly(copy);
    }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Sql.Schema.SqlCompiledSchema _target;

        public DebugView(global::Assimalign.Cohesion.Database.Sql.Schema.SqlCompiledSchema value)
        {
            _target = value;
        }

        public string Format => _target.Format;
        public string Name => _target.Name;
        public bool AllowsDestructiveChanges => _target.AllowsDestructiveChanges;
        public global::System.Collections.Generic.IReadOnlyList<global::Assimalign.Cohesion.Database.Sql.Schema.CompiledSchemaType> Types => _target.Types;
        public global::System.Collections.Generic.IReadOnlyList<global::Assimalign.Cohesion.Database.Sql.Schema.CompiledSchemaTable> Tables => _target.Tables;
        public global::System.Collections.Generic.IReadOnlyList<global::Assimalign.Cohesion.Database.Sql.Schema.CompiledSchemaPrincipal> Principals => _target.Principals;
    }
}
