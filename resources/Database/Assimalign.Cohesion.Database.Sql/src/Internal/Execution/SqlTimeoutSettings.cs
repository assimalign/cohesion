using System;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal static class SqlTimeoutSettings
{
    internal static void Validate(TimeSpan value, string parameterName)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(value, TimeSpan.Zero, parameterName);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, TimeSpan.FromMilliseconds(int.MaxValue), parameterName);
    }
}
