using System;

using Xunit;

namespace Assimalign.Cohesion.Sdk.ApplicationModel.Tests;

/// <summary>Limits the non-Linux NativeAOT rejection assertion to hosts that cannot use the native Linux route.</summary>
// Deviates from the repo interface-first rule per work-item requirements: xUnit v2 discovers FactAttribute subclasses.
public sealed class NonLinuxImageFactAttribute : FactAttribute
{
    /// <summary>Records the platform-specific skip reason at discovery time.</summary>
    public NonLinuxImageFactAttribute()
    {
        if (OperatingSystem.IsLinux())
        {
            Skip = "This assertion covers the non-Linux host NativeAOT route; Linux can build natively.";
        }
    }
}
