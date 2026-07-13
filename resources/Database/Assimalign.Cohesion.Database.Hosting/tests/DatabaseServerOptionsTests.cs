using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Hosting.Tests;

public class DatabaseServerOptionsTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Server: Options carry safe defaults")]
    public void Options_Defaults_ShouldBeBounded()
    {
        // Arrange
        var options = new DatabaseServerOptions();

        // Assert
        options.MaxSessions.ShouldBeGreaterThan(0);
        options.AuthenticationTimeout.ShouldBeGreaterThan(TimeSpan.Zero);
        options.IdleTimeout.ShouldBeGreaterThan(TimeSpan.Zero);
        options.ShutdownDrainTimeout.ShouldBeGreaterThan(TimeSpan.Zero);
    }
}
