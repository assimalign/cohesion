using System.Collections.Generic;

using Assimalign.Cohesion.Database.Sql.Catalog;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed record SqlSystemViewDefinition(string Schema, string Name, IReadOnlyList<SqlCatalogColumn> Columns);
