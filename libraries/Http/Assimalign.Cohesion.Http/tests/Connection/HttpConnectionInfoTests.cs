using System.Net;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Tests;

public class HttpConnectionInfoTests
{
    [Fact]
    public void Constructor_IPEndPoints_ShouldExposeDerivedAddressAndPortInformation()
    {
        // Arrange
        IPEndPoint local = new(IPAddress.Loopback, 8080);
        IPEndPoint remote = new(IPAddress.Parse("192.168.1.20"), 52341);

        // Act
        HttpConnectionInfo info = new(local, remote, isSecure: true);

        // Assert
        info.LocalEndPoint.ShouldBe(local);
        info.RemoteEndPoint.ShouldBe(remote);
        info.LocalIp.ShouldBe(local.Address);
        info.RemoteIp.ShouldBe(remote.Address);
        info.LocalPort.ShouldBe(8080);
        info.RemotePort.ShouldBe(52341);
        info.IsSecure.ShouldBeTrue();
    }
}
