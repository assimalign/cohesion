using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Client.Tests;

public sealed class DatabaseCommandClientTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Client] - Create: Disposing a client retains its caller-owned transport")]
    public void Create_WithCallerOwnedTransport_ShouldNotDisposeIt()
    {
        // Arrange
        using var transport = new RecordingHttpMessageInvoker();
        using IDatabaseCommandClient client = DatabaseCommandClient.Create(
            new Uri("https://resource.test:8443/custom/control"), "bootstrap-token", transport);

        // Act
        client.Dispose();

        // Assert
        transport.IsDisposed.ShouldBeFalse();
    }
}
