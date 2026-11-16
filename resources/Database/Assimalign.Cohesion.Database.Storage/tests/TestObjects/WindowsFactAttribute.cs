using System;

using Xunit;

namespace Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

internal sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute() { if (!OperatingSystem.IsWindows()) { Skip = "Requires Windows junctions or sharing semantics."; } }
}
