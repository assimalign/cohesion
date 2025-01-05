using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.FileSystem.Globbing.Internal.Utilities;

internal static class StringComparisonHelper
{
    public static StringComparer GetStringComparer(StringComparison comparisonType)
    {
        switch (comparisonType)
        {
            case StringComparison.CurrentCulture:
                return StringComparer.CurrentCulture;
            case StringComparison.CurrentCultureIgnoreCase:
                return StringComparer.CurrentCultureIgnoreCase;
            case StringComparison.Ordinal:
                return StringComparer.Ordinal;
            case StringComparison.OrdinalIgnoreCase:
                return StringComparer.OrdinalIgnoreCase;
            case StringComparison.InvariantCulture:
                return StringComparer.InvariantCulture;
            case StringComparison.InvariantCultureIgnoreCase:
                return StringComparer.InvariantCultureIgnoreCase;
            default:
                throw new InvalidOperationException();// SR.Format(SR.UnexpectedStringComparisonType, comparisonType));
        }
    }
}
