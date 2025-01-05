using System;

namespace Assimalign.Cohesion.FileSystem.Globbing.Internal.Utilities;

internal static class StringComparisonHelper
{
    public static StringComparer GetStringComparer(StringComparison comparisonType)
    {
        return comparisonType switch
        {
            StringComparison.CurrentCulture => StringComparer.CurrentCulture,
            StringComparison.CurrentCultureIgnoreCase => StringComparer.CurrentCultureIgnoreCase,
            StringComparison.Ordinal => StringComparer.Ordinal,
            StringComparison.OrdinalIgnoreCase => StringComparer.OrdinalIgnoreCase,
            StringComparison.InvariantCulture => StringComparer.InvariantCulture,
            StringComparison.InvariantCultureIgnoreCase => StringComparer.InvariantCultureIgnoreCase,
         
            _ =>  throw new InvalidOperationException()// SR.Format(SR.UnexpectedStringComparisonType, comparisonType));
        };
    }
}
