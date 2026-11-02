using System;

namespace Assimalign.Cohesion.Database.Documents.Language.Internal;

internal static class OqlSourceName
{
    internal static string Implicit(string collection)
        => string.Equals(collection, "COHESION_SCHEMA.INDEXES", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(collection, "COHESION_SCHEMA.OBJECT_OWNERSHIP", StringComparison.OrdinalIgnoreCase)
                ? collection[(collection.LastIndexOf('.') + 1)..] : collection;
}
