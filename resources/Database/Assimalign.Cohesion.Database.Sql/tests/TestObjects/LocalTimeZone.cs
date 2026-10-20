using System;
using System.Runtime.CompilerServices;

namespace Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

/// <summary>Changes only a child test process's local zone, on Windows as well as Unix.</summary>
internal static class LocalTimeZone
{
    internal static void Set(TimeZoneInfo zone)
    {
        // .NET 10 TimeZoneInfo.cs:42,46-48,880: Local is a cached instance with these fields.
        // https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/System/TimeZoneInfo.cs
        // Change its rules only in an isolated child, then ClearCachedData restores the OS zone.
        // No OS-wide setting, reflection, shipped member or visibility grant is involved.
        TimeZoneInfo.ClearCachedData();
        var local = TimeZoneInfo.Local;
        BaseUtcOffset(local) = zone.BaseUtcOffset;
        AdjustmentRules(local) = null;
        SupportsDaylightSavingTime(local) = false;
        Id(local) = zone.Id;
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_baseUtcOffset")]
    private static extern ref TimeSpan BaseUtcOffset(TimeZoneInfo zone);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_adjustmentRules")]
    private static extern ref TimeZoneInfo.AdjustmentRule[]? AdjustmentRules(TimeZoneInfo zone);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_supportsDaylightSavingTime")]
    private static extern ref bool SupportsDaylightSavingTime(TimeZoneInfo zone);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_id")]
    private static extern ref string Id(TimeZoneInfo zone);
}
