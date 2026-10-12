using System;

using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

internal sealed class WindowsProcessTheoryAttribute : TheoryAttribute
{
    public WindowsProcessTheoryAttribute(bool longPath = false)
    {
        Skip = longPath
            ? new WindowsLongPathFactAttribute().Skip
            : OperatingSystem.IsWindows() ? null : "Requires Windows process path semantics.";
    }
}
