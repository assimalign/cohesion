using System;

namespace Assimalign.Cohesion.Configuration;

public enum KeyComparison
{
    Ordinal = StringComparison.Ordinal,
    OrdinalIgnoreCase = StringComparison.OrdinalIgnoreCase,
    CurrentCulture = StringComparison.CurrentCulture,
    CurrentCultureIgnoreCase = StringComparison.CurrentCultureIgnoreCase,
    InvariantCulture = StringComparison.InvariantCulture,
    InvariantCultureIgnoreCase = StringComparison.InvariantCultureIgnoreCase,
}
