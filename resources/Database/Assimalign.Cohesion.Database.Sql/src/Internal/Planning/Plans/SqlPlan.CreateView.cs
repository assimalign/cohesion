using Assimalign.Cohesion.Database.Sql.Catalog;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>Publishes a canonical read-only view.</summary>
internal sealed record SqlCreateViewPlan(SqlCatalogView View, bool OrReplace) : SqlPlan;
