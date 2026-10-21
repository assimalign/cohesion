using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Graph.Storage.Tests;

internal static class TestLabelIds
{
    internal static readonly IReadOnlyDictionary<string, Guid> All = new Dictionary<string, Guid>(StringComparer.Ordinal)
    {
        ["N"] = new("10000000-0000-0000-0000-000000000001"),
        ["New"] = new("10000000-0000-0000-0000-000000000002"),
        ["Long"] = new("10000000-0000-0000-0000-000000000005"),
        ["M"] = new("10000000-0000-0000-0000-000000000003"),
        ["Person"] = new("10000000-0000-0000-0000-000000000004"),
    };
}
