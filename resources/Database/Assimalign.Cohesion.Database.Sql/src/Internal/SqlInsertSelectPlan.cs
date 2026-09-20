using System.Collections.Generic;

using Assimalign.Cohesion.Database.Sql.Catalog;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// Inserts a complete query result into a bound target. The executor materializes
/// the source with the statement snapshot before applying any destination write.
/// </summary>
internal sealed record SqlInsertSelectPlan(
    SqlCatalogTable Table,
    IReadOnlyList<int> TargetOrdinals,
    SqlPlan Source) : SqlPlan;
