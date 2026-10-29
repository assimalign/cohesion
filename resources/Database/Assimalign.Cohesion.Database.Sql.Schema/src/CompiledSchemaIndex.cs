using System.Collections.Generic;

using Assimalign.Cohesion.Database.Sql.Schema.Internal;

namespace Assimalign.Cohesion.Database.Sql.Schema;

/// <summary>Describes a compiled secondary index.</summary>
/// <param name="Name">The stable index name.</param>
/// <param name="Columns">The ordered index columns.</param>
/// <param name="Predicate">The optional SQL row predicate; validated by the engine before schema application.</param>
/// <param name="IsUnique">Whether the index enforces uniqueness.</param>
public sealed record CompiledSchemaIndex(string Name, IReadOnlyList<string> Columns, bool IsUnique = false, string? Predicate = null)
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
