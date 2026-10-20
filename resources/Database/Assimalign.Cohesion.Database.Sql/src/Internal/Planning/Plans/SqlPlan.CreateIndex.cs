using System.Collections.Generic;

using Assimalign.Cohesion.Database.Sql.Catalog;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed record SqlCreateIndexPlan(
    SqlCatalogTable Table,
    string IndexName,
    IReadOnlyList<string> ColumnNames,
    bool IsUnique,
    bool IfNotExists) : SqlPlan;
