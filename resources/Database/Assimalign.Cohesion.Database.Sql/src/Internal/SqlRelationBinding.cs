using System.Collections.Generic;

using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>A relation's source, qualifier and position in a joined row.</summary>
internal sealed record SqlRelationBinding(SqlRelationSource Source, SqlRelationReference Reference, int Offset)
{
    // USING hides only unqualified right columns, never their qualified references.
    internal HashSet<int> HiddenColumns { get; } = [];

    /// <summary>Merged USING expressions keyed by their preserved physical column ordinal.</summary>
    internal Dictionary<int, SqlUsingColumn>? UsingColumns { get; set; }
}
