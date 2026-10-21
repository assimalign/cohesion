using System.Collections.Generic;
using System.Text.Json.Serialization;

using Assimalign.Cohesion.Database.Sql.Schema.Internal;

namespace Assimalign.Cohesion.Database.Sql.Schema;

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
