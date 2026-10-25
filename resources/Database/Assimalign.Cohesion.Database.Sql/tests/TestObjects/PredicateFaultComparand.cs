using System;

using Assimalign.Cohesion.Database.Sql.Internal;

namespace Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

internal sealed class PredicateFaultComparand : IComparable
{
    public int CompareTo(object? obj)
        => throw new SqlPlanningException("CS02003", "Comparison operand refused by the test comparand.");
}
