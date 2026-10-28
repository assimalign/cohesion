using System.Collections.Generic;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

// Override storage belongs only to plans that carry an explicit identity policy.
// The ordinary base plan retains its original layout and debugger view.
internal sealed record SqlIdentityInsertPlan : SqlInsertPlan
{
    internal SqlIdentityInsertPlan(SqlCatalogTable table, IReadOnlyList<int> targetOrdinals,
        IReadOnlyList<SqlBoundExpression[]> rows, SqlIdentityOverride overriding)
        : base(table, targetOrdinals, rows) { IdentityOverride = overriding; }

    internal SqlIdentityOverride IdentityOverride { get; }
}
