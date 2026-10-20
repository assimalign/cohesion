using System.Collections.Generic;

using Assimalign.Cohesion.Database.Sql.Schema.Internal;

namespace Assimalign.Cohesion.Database.Sql.Schema;

/// <summary>Describes a compiled key.</summary>
/// <param name="Name">The stable key name.</param>
/// <param name="Columns">The ordered key columns.</param>
public sealed record CompiledSchemaKey(string Name, IReadOnlyList<string> Columns)
{
    /// <summary>Gets the immutable ordered key columns.</summary>
    public IReadOnlyList<string> Columns { get; } = CompiledSchemaSnapshots.Copy(Columns);
}
