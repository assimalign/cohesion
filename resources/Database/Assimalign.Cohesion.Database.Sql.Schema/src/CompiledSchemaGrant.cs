using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Database.Sql.Schema.Internal;

namespace Assimalign.Cohesion.Database.Sql.Schema;

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
