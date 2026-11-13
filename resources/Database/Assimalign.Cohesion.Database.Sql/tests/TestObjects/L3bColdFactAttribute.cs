using System;

using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

internal sealed class L3bColdFactAttribute : FactAttribute
{
    public L3bColdFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("L3B_COLD_SHAPE") is null)
        { Skip = "The evidence runner sets L3B_COLD_SHAPE in a fresh process."; }
    }
}
