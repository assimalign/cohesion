using System;

using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

/// <summary>Reports local-zone isolation cases as skipped where TZ is unsupported.</summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class UnixTimeZoneFactAttribute : FactAttribute
{
    public UnixTimeZoneFactAttribute()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "Windows has no supported per-process local time-zone override; "
                + "the Linux CI leg is authoritative for this regression (#1648).";
        }
    }
}
