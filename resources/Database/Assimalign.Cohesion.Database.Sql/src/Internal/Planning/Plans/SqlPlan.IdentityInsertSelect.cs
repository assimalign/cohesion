using System.Collections.Generic;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed record SqlIdentityInsertSelectPlan : SqlInsertSelectPlan
{
    internal SqlIdentityInsertSelectPlan(SqlCatalogTable table, IReadOnlyList<int> targetOrdinals,
        SqlPlan source, SqlIdentityOverride overriding) : base(table, targetOrdinals, source)
    { IdentityOverride = overriding; }

    internal SqlIdentityOverride IdentityOverride { get; }
}
