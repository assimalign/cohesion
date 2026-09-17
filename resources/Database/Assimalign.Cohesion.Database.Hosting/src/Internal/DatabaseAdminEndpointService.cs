using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Connections.Security;
using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Health;
using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Http.Connections;
using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.Health;
using Assimalign.Cohesion.Web.Hosting;
using Assimalign.Cohesion.Web.Hosting.Health;
using Assimalign.Cohesion.Web.Hosting.Resources;

namespace Assimalign.Cohesion.Database.Hosting;

internal sealed class DatabaseAdminEndpointService : BackgroundService, IHostService, IDisposable
{
    private X509Certificate2? _serverCertificate;
    private X509Certificate2Collection _serverCertificateChain = new();
    private readonly WebApplication _application;

    internal DatabaseAdminEndpointService(
        Uri endpoint,
        IResourceControlPlane controlPlane,
        ResourceContext resourceContext,
        DatabaseApplicationContext applicationContext,
        IReadOnlyList<IHealthContributor> healthContributors)
    {
        Uri.ThrowIfNotEndpoint(endpoint);
        ArgumentNullException.ThrowIfNull(controlPlane);
        ArgumentNullException.ThrowIfNull(resourceContext);
        ArgumentNullException.ThrowIfNull(applicationContext);
        ArgumentNullException.ThrowIfNull(healthContributors);

        if (!string.Equals(endpoint.Scheme, "http", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(endpoint.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The ambient Database endpoint 'admin' must use http or https, not '{endpoint.Scheme}'.");
        }

        IPAddress address = ResolveBindAddress(endpoint.IdnHost);
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        if (string.Equals(endpoint.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            if (ResourceRuntime.Current is not ResourceContext certificateContext ||
                !certificateContext.TryGetEndpointCertificate("admin", out _serverCertificate, out _serverCertificateChain))
            {
                throw new InvalidOperationException("The Database https endpoint 'admin' requires its certificate Secret mount (default 'tls').");
            }
            SslStreamCertificateContext certificate = SslStreamCertificateContext.Create(_serverCertificate, _serverCertificateChain, offline: true);
            builder.Server.UseServer(options => options.UseHttp1s(
                tcp => tcp.EndPoint = new IPEndPoint(address, endpoint.Port),
                new TlsServerOptions { AuthenticationOptions = new SslServerAuthenticationOptions { ServerCertificateContext = certificate } }));
        }
        else
        {
            builder.Server.UseServer(options => options.UseHttp1(tcp => tcp.EndPoint = new IPEndPoint(address, endpoint.Port)));
        }

        _application = builder.Build();
        IWebApplicationPipelineBuilder pipeline = _application;
        IHealthCheckService health = CreateHealthService(
            applicationContext,
            healthContributors);
        pipeline.MapHealthChecks(
            new HttpPath("/healthz"),
            health);
        pipeline.MapReadinessCheck(health, new HttpPath("/readyz"));
        pipeline.MapLivenessCheck(health, new HttpPath("/livez"));
        pipeline.MapHealthChecks(
            new HttpPath("/cohesion/v1/healthz"),
            health);
        pipeline.MapReadinessCheck(health, new HttpPath("/cohesion/v1/readyz"));
        pipeline.MapLivenessCheck(health, new HttpPath("/cohesion/v1/livez"));
        pipeline.Use(next => context => ResourceControlPlaneMiddleware.InvokeAsync(
            controlPlane, resourceContext, isApplicationReady: true, endpoint.Port, context, next));
    }

    public new void Dispose()
    {
        base.Dispose();
        ((IDisposable)_application).Dispose();
        _serverCertificate?.Dispose();
        foreach (X509Certificate2 certificate in _serverCertificateChain)
        {
            certificate.Dispose();
        }
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

    async Task IHostService.StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Stop the nested listener inside the outer host's shutdown budget before
            // cancelling the parking loop. ExecuteAsync's finally remains a fallback for
            // disposal and failed-start compensation, where no service-stop token exists.
            await ((IHost)_application).StopAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await base.StopAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static IHealthCheckService CreateHealthService(
        DatabaseApplicationContext applicationContext,
        IReadOnlyList<IHealthContributor> contributors)
    {
        IHealthChecksBuilder builder = HealthChecks.CreateBuilder();
        foreach (IHealthContributor contributor in contributors)
        {
            builder.AddContributor(contributor);
        }

        // The private admin web host starts before the wire-protocol servers so it can report
        // startup progress. Readiness must therefore carry an explicit outer-host gate: it does
        // not turn healthy until every server's StartAsync has confirmed that it is accepting.
        // This check is readiness-only so liveness continues to describe the process itself.
        builder.AddCheck(
            "database.accepting",
            () => applicationContext.State == HostState.Started
                ? HealthCheckResult.Healthy("All database servers are accepting connections.")
                : HealthCheckResult.Unhealthy(
                    $"The database application is '{applicationContext.State}' and is not accepting connections."),
            tags: [HealthTags.Ready]);

        return builder.Build();
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
