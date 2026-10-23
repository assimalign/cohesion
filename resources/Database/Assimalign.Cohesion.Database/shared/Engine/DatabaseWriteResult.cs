using System;
using System.Collections.Generic;

// Deviates from namespace-matches-assembly: model-local linked source.
namespace Assimalign.Cohesion.Database;

internal struct DatabaseWriteResult
{
    internal int Units;
    internal List<Exception>? Failures;
}
