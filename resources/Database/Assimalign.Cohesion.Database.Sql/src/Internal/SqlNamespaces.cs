using System;
using System.Globalization;

using Assimalign.Cohesion.Database.Sql.Catalog;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>The engine's SQL namespace resolution and creation rule.</summary>
internal static class SqlNamespaces
{
    internal const string DefaultName = Language.Internal.SqlNamespaceNames.Default;

    internal static bool IsReserved(string name)
        => name.Equals("INFORMATION_SCHEMA", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("COHESION_SCHEMA", StringComparison.OrdinalIgnoreCase);

    internal static SqlCatalogNamespace? Find(SqlCatalog catalog, string name)
    {
        var namespaces = catalog.Namespaces;
        for (int index = 0; index < namespaces.Count; index++)
        {
            if (namespaces[index].Name.Equals(name, StringComparison.OrdinalIgnoreCase)) { return namespaces[index]; }
        }
        return null;
    }

    internal static SqlCatalogNamespace Require(SqlCatalog catalog, string name)
        => Find(catalog, name) ?? throw new SqlPlanningException("COHSQLE028",
            string.Format(CultureInfo.CurrentCulture, ErrorMessages.EngineNamespaceMissing, name));
}
