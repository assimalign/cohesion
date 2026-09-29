using System.Net;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Hosting;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Web.Hosting.Tests;

public class WebApplicationServerLifecycleTests
{
    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Listener lifecycle: Start stop start should reuse the same TCP port in one process")]
    public async Task Application_StartStopStartWithFreshHost_ShouldReuseSamePort()
    {
        // Arrange — let the first bind choose an unused port, then pin every later host to that
        // exact endpoint. Starting through IWebApplication exercises the default server's
        // IHostService registration rather than calling the server implementation directly.
        TcpConnectionListener firstListener = CreateTcpListener(new IPEndPoint(IPAddress.Loopback, 0));
        await using WebApplication firstApplication = CreateApplication(firstListener);

        // Act — the first application must not report Started until the port is reserved.
        await ((IWebApplication)firstApplication).StartAsync();
        IPEndPoint boundEndPoint = (IPEndPoint)firstListener.EndPoint;
        boundEndPoint.Port.ShouldBeGreaterThan(0);

        // Assert — a competing application fails during awaited startup, and the transport
        // failure is carried by the typed host-startup envelope used by ResourceHost exit mapping.
        TcpConnectionListener competingListener = CreateTcpListener(boundEndPoint);
        await using WebApplication competingApplication = CreateApplication(competingListener);

        HostStartupException bindFailure = await Should.ThrowAsync<HostStartupException>(
            () => ((IWebApplication)competingApplication).StartAsync());
        bindFailure.InnerException.ShouldNotBeNull();

        // Act — StopAsync is the release boundary. A fresh application can bind the exact same
        // endpoint before this test process exits, matching the member-restart model.
        await ((IWebApplication)firstApplication).StopAsync();

        TcpConnectionListener restartedListener = CreateTcpListener(boundEndPoint);
        await using WebApplication restartedApplication = CreateApplication(restartedListener);
        await ((IWebApplication)restartedApplication).StartAsync();

        // Assert
        restartedListener.EndPoint.ShouldBe(boundEndPoint);
        await ((IWebApplication)restartedApplication).StopAsync();
    }

    private static TcpConnectionListener CreateTcpListener(IPEndPoint endPoint)
    {
        return new TcpConnectionListener(new TcpConnectionListenerOptions
        {
            EndPoint = endPoint
        });
    }

    private static WebApplication CreateApplication(TcpConnectionListener listener)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Server.UseServer(options => options.UseHttp1(listener));

        return builder.Build();
    }
}
