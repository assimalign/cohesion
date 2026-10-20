using System;
using System.Diagnostics;

namespace Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

/// <summary>Configures an isolated Unix test process's local zone before it starts.</summary>
internal static class LocalTimeZone
{
    internal static void Set(ProcessStartInfo start, int hours)
    {
        // Unix TimeZoneInfo.Local reads TZ; vstest's test host inherits this environment.
        start.Environment["TZ"] = GetId(hours);
    }

    internal static string GetId(int hours) => hours switch
    {
        -7 => "America/Phoenix",
        9 => "Asia/Tokyo",
        _ => throw new ArgumentOutOfRangeException(nameof(hours)),
    };
}
