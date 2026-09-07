using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Core;
using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Http.Connections;
using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.Health;
using Assimalign.Cohesion.Web.Hosting;

using HostingHealthStatus = Assimalign.Cohesion.Hosting.HealthStatus;

namespace Assimalign.Cohesion.Database.Hosting.Internal;

internal sealed class DatabaseAdminEndpointService : BackgroundService
{
    private readonly WebApplication _application;

    internal DatabaseAdminEndpointService(
        EndpointAddress endpoint,
        IResourceControlPlane controlPlane)
    {
        ArgumentNullException.ThrowIfNull(controlPlane);

        if (!string.Equals(endpoint.Scheme, "http", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The ambient Database endpoint 'admin' must use the http scheme, not '{endpoint.Scheme}'.");
        }

        IPAddress address = ResolveBindAddress(endpoint.Host);
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Server.UseServer(options =>
            options.UseHttp1(tcp => tcp.EndPoint = new IPEndPoint(address, endpoint.Port)));

        _application = builder.Build();
        IWebApplicationPipelineBuilder pipeline = _application;
        pipeline.MapHealthChecks(
            new HttpPath("/healthz"),
            CreateHealthService(controlPlane.CheckHealthAsync));
        pipeline.MapHealthChecks(
            new HttpPath("/readyz"),
            CreateHealthService(controlPlane.CheckReadinessAsync));
        pipeline.MapHealthChecks(
            new HttpPath("/livez"),
            CreateHealthService(controlPlane.CheckLivenessAsync));
    }

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        await ((IHost)_application).StartAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            await ((IHost)_application).StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static IHealthCheckService CreateHealthService(
        Func<CancellationToken, ValueTask<ResourceHealthReport>> check)
    {
        return HealthChecks.CreateBuilder()
            .AddCheck("resource", async (_, cancellationToken) =>
            {
                ResourceHealthReport report = await check.Invoke(cancellationToken).ConfigureAwait(false);
                return report.Status switch
                {
                    HostingHealthStatus.Healthy => HealthCheckResult.Healthy(),
                    HostingHealthStatus.Degraded => HealthCheckResult.Degraded(),
                    _ => HealthCheckResult.Unhealthy(),
                };
            })
            .Build();
    }

    private static IPAddress ResolveBindAddress(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return IPAddress.Loopback;
        }

        return IPAddress.TryParse(host, out IPAddress? address)
            ? address
            : throw new InvalidOperationException(
                $"The ambient Database admin endpoint host '{host}' is not a bindable IP address.");
    }
}
