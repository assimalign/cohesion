using System;
using System.Collections.Generic;
using System.Net;
using System.Security.Cryptography;
using System.Text;
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
using Assimalign.Cohesion.Web.Hosting.Internal;

using HostingHealthStatus = Assimalign.Cohesion.Hosting.HealthStatus;
using CohesionHttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;

namespace Assimalign.Cohesion.Database.Hosting;

internal sealed class DatabaseAdminEndpointService : BackgroundService, IHostService
{
    private readonly WebApplication _application;

    internal DatabaseAdminEndpointService(
        EndpointAddress endpoint,
        IResourceControlPlane controlPlane,
        ReadOnlyMemory<byte> bootstrapCredential,
        bool requireAuthentication,
        DatabaseApplicationContext applicationContext,
        IReadOnlyList<IHealthContributor> healthContributors)
    {
        ArgumentNullException.ThrowIfNull(controlPlane);
        ArgumentNullException.ThrowIfNull(applicationContext);
        ArgumentNullException.ThrowIfNull(healthContributors);

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
        IHealthCheckService health = CreateHealthService(
            applicationContext,
            healthContributors);
        ReadOnlyMemory<byte> credential = bootstrapCredential.ToArray();
        pipeline.Use(async (context, next) =>
        {
            if (context.Request.Path.Value.StartsWith(
                    "/cohesion/v1",
                    StringComparison.Ordinal) &&
                !IsAuthorized(context, credential.Span, requireAuthentication))
            {
                context.Response.StatusCode = CohesionHttpStatusCode.Unauthorized;
                context.Response.Headers[HttpHeaderKey.WWWAuthenticate] = "Bearer";
                return;
            }

            await next.Invoke(context).ConfigureAwait(false);
        });
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
        pipeline.Use(next => context =>
        {
            string path = context.Request.Path.Value;
            return path is "/cohesion/v1/endpoints" or "/cohesion/v1/stop" or "/cohesion/v1/commands"
                ? ResourceControlPlaneMiddleware.InvokeAsync(
                    controlPlane,
                    endpoint.Port,
                    context,
                    next)
                : next.Invoke(context);
        });
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
            builder.AddCheck(contributor.Name, async (_, cancellationToken) =>
            {
                HealthContribution contribution = await contributor
                    .CheckAsync(cancellationToken)
                    .ConfigureAwait(false);
                return contribution.Status switch
                {
                    HostingHealthStatus.Healthy => HealthCheckResult.Healthy(
                        contribution.Description,
                        contribution.Data),
                    HostingHealthStatus.Degraded => HealthCheckResult.Degraded(
                        contribution.Description,
                        data: contribution.Data),
                    _ => HealthCheckResult.Unhealthy(
                        contribution.Description,
                        data: contribution.Data),
                };
            }, tags: [HealthTags.Ready, HealthTags.Live]);
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

    private static bool IsAuthorized(
        IHttpContext context,
        ReadOnlySpan<byte> bootstrapCredential,
        bool requireAuthentication)
    {
        if (bootstrapCredential.IsEmpty)
        {
            return !requireAuthentication;
        }

        if (!context.Request.Headers.TryGetValue(
                HttpHeaderKey.Authorization,
                out HttpHeaderValue authorization))
        {
            return false;
        }

        const string bearerPrefix = "Bearer ";
        string value = authorization.Value;
        if (!value.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        byte[] presentedCredential = Encoding.UTF8.GetBytes(value[bearerPrefix.Length..]);
        return CryptographicOperations.FixedTimeEquals(
            presentedCredential,
            bootstrapCredential);
    }
}
