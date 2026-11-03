using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Database.Types;
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
internal sealed class DatabaseAdminEndpointService : IHostService, IDisposable, IAsyncDisposable
{
    private static readonly string[] _databaseHealthTags = [HealthTags.Ready, HealthTags.Live];

    private readonly WebApplication _application;
    private readonly Uri _endpoint;

    internal DatabaseAdminEndpointService(
        Uri endpoint,
        DatabaseApplicationContext applicationContext)
    {
        IPEndPoint bindEndPoint = GetBindEndPoint(endpoint);
        _endpoint = endpoint;
        ArgumentNullException.ThrowIfNull(applicationContext);
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Server.UseServer(options => options.UseHttp1(tcp => tcp.EndPoint = bindEndPoint));

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

    public ServiceId Id { get; } = ServiceId.New();

    public void Dispose()
    {
        try
        {
            ((IDisposable)_application).Dispose();
        }
        catch (Exception exception)
        {
            // Record an infrastructure failure, preserving the existing disposal failure.
            DatabaseHostingEventSource.Log.AdminEndpointFailed(_endpoint, "Dispose", exception);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await ((IAsyncDisposable)_application).DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Disposal is an infrastructure boundary; report and preserve its failure.
            DatabaseHostingEventSource.Log.AdminEndpointFailed(_endpoint, "Dispose", exception);
            throw;
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await ((IHost)_application).StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is SocketException or HostStartupException { InnerException: SocketException })
        {
            // Binding is awaited before model servers start; preserve the transport cause while
            // giving the Database caller a structured diagnostic for the failed health bind.
            DatabaseHostingEventSource.Log.AdminEndpointFailed(_endpoint, "Start", exception);
            string message = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                ErrorMessages.HealthEndpointBindFailed, _endpoint.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped));
            throw new DatabaseException(message,
                [new Diagnostic { Code = "CK07102", Message = message, Severity = DiagnosticSeverity.Error }], exception);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            DatabaseHostingEventSource.Log.AdminEndpointFailed(_endpoint, "Start", exception);
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await ((IHost)_application).StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            DatabaseHostingEventSource.Log.AdminEndpointFailed(_endpoint, "Stop", exception);
            throw;
        }
    }

    internal static bool CanBindPlatformEndpoint(Uri endpoint)
        => string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && (string.Equals(endpoint.IdnHost, "localhost", StringComparison.OrdinalIgnoreCase)
                || IPAddress.TryParse(endpoint.IdnHost, out _));

    internal static IPEndPoint GetBindEndPoint(Uri endpoint)
    {
        Uri.ThrowIfNotEndpoint(endpoint);
        if (string.Equals(endpoint.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            // The retired resource context supplied the certificate. No current source exists.
            throw new InvalidOperationException(ErrorMessages.DatabaseAdminEndpointServiceFailure2);
        }

        if (!string.Equals(endpoint.Scheme, "http", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.DatabaseAdminEndpointServiceFailure, endpoint.Scheme));
        }

        return new IPEndPoint(ResolveBindAddress(endpoint.IdnHost), endpoint.Port);
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
