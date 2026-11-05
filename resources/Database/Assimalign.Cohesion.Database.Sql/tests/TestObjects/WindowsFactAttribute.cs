using System;

using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

internal sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute() { if (!OperatingSystem.IsWindows()) { Skip = "Requires Windows junctions."; } }
}
