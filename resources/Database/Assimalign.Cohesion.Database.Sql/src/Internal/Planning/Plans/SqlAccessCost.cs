using System.Collections.Generic;
using Assimalign.Cohesion.Database.Indexing;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>Transient alternatives and bounded probe evidence; never persisted.</summary>
internal sealed class SqlAccessCost
{
    internal SqlAccessCost(IReadOnlyList<SqlIndexSeekPath> alternatives)
    {
        Alternatives = alternatives;
    }

    internal IReadOnlyList<SqlIndexSeekPath> Alternatives { get; }
    internal BTreeRangeCount? Estimate { get; set; }
}
