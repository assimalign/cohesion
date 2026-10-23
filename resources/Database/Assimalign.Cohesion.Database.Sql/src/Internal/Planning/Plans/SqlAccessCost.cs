using System.Collections.Generic;
using Assimalign.Cohesion.Database.Indexing;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>Transient alternatives and bounded probe evidence; never persisted.</summary>
internal sealed class SqlAccessCost(IReadOnlyList<SqlIndexSeekPath> alternatives)
{
    internal IReadOnlyList<SqlIndexSeekPath> Alternatives { get; } = alternatives;
    internal BTreeRangeCount? Estimate { get; set; }
}
