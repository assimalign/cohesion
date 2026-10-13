using System;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Hosting.Tests;

public sealed class WebApplicationServerInterceptorOrderTests
{
    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Server: Own interceptors precede HTTP defaults and configured interceptors")]
    public async Task Build_DefaultServer_ShouldPreserveTheChosenInterceptorOrder()
    {
        // Arrange — the transport factory runs while the public server snapshots its full list.
        await using InMemoryConnectionListener transport = new();
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        IHttpExchangeInterceptor[]? observed = null;
        ProbeInterceptor first = new();
        ProbeInterceptor second = new();
        builder.Server.UseServer(options =>
        {
            options.Interceptors.Add(first);
            options.UseHttp1(() =>
            {
                observed = options.Interceptors.ToArray();
                return transport;
            });
        });
        builder.Server.UseServer(options => options.Interceptors.Add(second));

        // Act
        await using WebApplication application = builder.Build();
        application.Context.Servers.ShouldHaveSingleItem().ShouldBeOfType<WebApplicationServer>();

        // Assert — the first two publish exchange-local state; HTTP defaults retain their order.
        observed.ShouldNotBeNull();
        observed!.Select(interceptor => interceptor.GetType().Name).ShouldBe(new[]
        {
            "WebClientFaultInterceptor",
            "WebResponseStartingInterceptor",
            "HttpRequestLimitsInterceptor",
            "HttpProtocolUpgradeInterceptor",
            "HttpExtendedConnectInterceptor",
            nameof(ProbeInterceptor),
            nameof(ProbeInterceptor),
        });
        observed[5].ShouldBeSameAs(first);
        observed[6].ShouldBeSameAs(second);
    }

    private sealed class ProbeInterceptor : HttpExchangeInterceptor
    {
        public override HttpInterceptorScopes Scopes => HttpInterceptorScopes.Request;
    }
}
