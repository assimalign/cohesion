using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Http.Connections;
using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.Health;
using Assimalign.Cohesion.Web.Hosting;

using HttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;

namespace Assimalign.Cohesion.Database.Hosting.Internal;

[global::System.Diagnostics.DebuggerDisplay("DatabaseAdminEndpointService")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Hosting.Internal.DatabaseAdminEndpointService.DebugView))]
internal sealed class DatabaseAdminEndpointService : BackgroundService, IHostService, IDisposable
{
    private static readonly string[] _databaseHealthTags = [HealthTags.Ready, HealthTags.Live];

    private readonly WebApplication _application;
    private readonly Uri _endpoint;

    internal DatabaseAdminEndpointService(
        Uri endpoint,
        DatabaseApplicationContext applicationContext)
    {
        Uri.ThrowIfNotEndpoint(endpoint);
        _endpoint = endpoint;
        ArgumentNullException.ThrowIfNull(applicationContext);

        if (!string.Equals(endpoint.Scheme, "http", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(endpoint.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.DatabaseAdminEndpointServiceFailure, endpoint.Scheme));
        }

        // An https endpoint's certificate came from the retired resource context (#1759). With no
        // source left for it, an https endpoint is refused rather than served without one.
        if (string.Equals(endpoint.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(ErrorMessages.DatabaseAdminEndpointServiceFailure2);
        }

        IPAddress address = ResolveBindAddress(endpoint.IdnHost);
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Server.UseServer(options => options.UseHttp1(tcp => tcp.EndPoint = new IPEndPoint(address, endpoint.Port)));

        _application = builder.Build();
        IWebApplicationPipelineBuilder pipeline = _application;
        IHealthCheckService health = CreateHealthService(applicationContext);
        pipeline.MapHealthChecks(
            new HttpPath("/healthz"),
            health);
        pipeline.MapReadinessCheck(health, new HttpPath("/readyz"));
        pipeline.MapLivenessCheck(health, new HttpPath("/livez"));
        // Database health aliases run before the closing guard to retain the database-specific
        // report and readiness gate. Apply locality before those aliases can short-circuit.
        pipeline.Use(next => context =>
        {
            if (IsNamespaced(context) && !IsLoopbackPeer(context))
            {
                context.Response.StatusCode = HttpStatusCode.Forbidden;
                return Task.CompletedTask;
            }

            return next(context);
        });

        pipeline.MapHealthChecks(
            new HttpPath("/cohesion/v1/healthz"),
            health);
        pipeline.MapReadinessCheck(health, new HttpPath("/cohesion/v1/readyz"));
        pipeline.MapLivenessCheck(health, new HttpPath("/cohesion/v1/livez"));
        // The rest of /cohesion/v1 was the resource control plane, which authenticated its callers
        // only with the retired resource bootstrap credential (#1759). Nothing can authorize those
        // calls any more, so they fail closed.
        pipeline.Use(next => context =>
        {
            if (IsNamespaced(context))
            {
                context.Response.StatusCode = HttpStatusCode.Forbidden;
                return Task.CompletedTask;
            }

            return next(context);
        });
    }

    internal IWebApplicationPipelineBuilder Pipeline => _application;

    public new void Dispose()
    {
        try
        {
            base.Dispose();
            ((IDisposable)_application).Dispose();
        }
        catch (Exception exception)
        {
            // Record an infrastructure failure, preserving the existing disposal failure.
            DatabaseHostingEventSource.Log.AdminEndpointFailed(_endpoint, "Dispose", exception);
            throw;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ((IHost)_application).StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            DatabaseHostingEventSource.Log.AdminEndpointFailed(_endpoint, "Start", exception);
            throw;
        }

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            try
            {
                await ((IHost)_application).StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                DatabaseHostingEventSource.Log.AdminEndpointFailed(_endpoint, "Stop", exception);
                throw;
            }
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
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            DatabaseHostingEventSource.Log.AdminEndpointFailed(_endpoint, "Stop", exception);
            throw;
        }
        finally
        {
            await base.StopAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static IHealthCheckService CreateHealthService(DatabaseApplicationContext applicationContext)
    {
        IHealthChecksBuilder builder = new HealthChecksBuilder();
        builder.AddCheck(
            DatabaseApplicationContext.HealthCheckName,
            (_, cancellationToken) => applicationContext.CheckAsync(cancellationToken),
            tags: _databaseHealthTags);

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

    private static bool IsNamespaced(IHttpContext context)
    {
        string path = context.Request.Path.Value;
        return path == "/cohesion/v1" || path.StartsWith("/cohesion/v1/", StringComparison.Ordinal);
    }

    private static bool IsLoopbackPeer(IHttpContext context)
    {
        if (context.ConnectionInfo.RemoteEndPoint is not IPEndPoint remoteEndPoint)
        {
            return false;
        }

        IPAddress address = remoteEndPoint.Address;
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return address.Equals(IPAddress.Loopback) || address.Equals(IPAddress.IPv6Loopback);
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
                string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ServiceResolveBindAddressFailure, host));
    }

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Hosting.Internal.DatabaseAdminEndpointService value)
        {
            _ = value;
        }
    }
}
