using System.Collections.Generic;

using Assimalign.Cohesion.Database.Sql.Schema.Internal;

namespace Assimalign.Cohesion.Database.Sql.Schema;

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
