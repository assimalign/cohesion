using System;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.Web.Routing.Internal;

internal static class RouteValueDictionaryTrimmerWarning
{
    public const string Warning = "This API may perform reflection on supplied parameters which may be trimmed if not referenced directly. " +
        "Initialize a RouteValueDictionary with route values to avoid this issue.";
}
