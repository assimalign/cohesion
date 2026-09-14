using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Rezolvr.Client.Tests;

public sealed class RezolvrCommandClientTests
{
    [Fact(DisplayName = "Cohesion Test [Rezolvr.Client] - Create: Disposing a client retains its caller-owned transport")]
    public void Create_WithCallerOwnedTransport_ShouldNotDisposeIt()
    {
        // Arrange
        using var transport = new RecordingHttpMessageInvoker();
        using IRezolvrCommandClient client = RezolvrCommandClient.Create(
            new Uri("https://resource.test:8443/custom/control"), "bootstrap-token", transport);

        // Act
        client.Dispose();

        // Assert
        transport.IsDisposed.ShouldBeFalse();
    }
}
