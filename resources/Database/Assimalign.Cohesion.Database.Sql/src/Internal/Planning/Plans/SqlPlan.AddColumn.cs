using System.Collections.Generic;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed record SqlAddColumnPlan(string Schema, string Name, SqlCatalogColumn Column, IReadOnlyList<SqlConstraintDefinition> Constraints) : SqlPlan;
