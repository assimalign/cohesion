using System;

using Xunit;

namespace Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

internal sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute() { if (OperatingSystem.IsWindows()) { Skip = "Requires Unix file modes."; } }
}
