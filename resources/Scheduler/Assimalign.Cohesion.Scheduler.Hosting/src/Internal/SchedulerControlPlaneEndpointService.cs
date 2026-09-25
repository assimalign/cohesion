using System;
using System.Buffers;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Core;
using Assimalign.Cohesion.Connections.Security;
using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Health;
using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Http.Connections;
using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.Hosting;

using CohesionHttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;

namespace Assimalign.Cohesion.Scheduler.Hosting.Internal;

internal sealed class SchedulerControlPlaneEndpointService : IHostService, IDisposable
{
    private X509Certificate2? _serverCertificate;
    private X509Certificate2Collection _serverCertificateChain = new();
    private readonly WebApplication _application;
    private readonly BootstrapTokenVerifier _bootstrapVerifier;
    private readonly string _resourceAudience;

    internal SchedulerControlPlaneEndpointService(
        Uri endpoint,
        IResourceControlPlane controlPlane,
        ResourceContext resourceContext,
        SchedulerApplicationContext applicationContext)
    {
        Uri.ThrowIfNotEndpoint(endpoint);
        ArgumentNullException.ThrowIfNull(controlPlane);
        ArgumentNullException.ThrowIfNull(resourceContext);
        ArgumentNullException.ThrowIfNull(applicationContext);

        if (!string.Equals(endpoint.Scheme, "http", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(endpoint.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The ambient Scheduler endpoint 'http' must use http or https, not '{endpoint.Scheme}'.");
        }

        if (resourceContext.GatewayName is null)
        {
            throw new InvalidOperationException(
                "A Scheduler control-plane endpoint requires an ambient gateway identity.");
        }

        _resourceAudience = resourceContext.ResourceName ?? string.Empty;
        if (string.IsNullOrWhiteSpace(_resourceAudience))
        {
            throw new InvalidOperationException(
                "A gateway-managed Scheduler requires an ambient resource name.");
        }

        _bootstrapVerifier = new BootstrapTokenVerifier(resourceContext);

        IPAddress address = ResolveBindAddress(endpoint.IdnHost);
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        if (string.Equals(endpoint.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            if (resourceContext is not ResourceContext certificateContext ||
                !certificateContext.TryGetEndpointCertificate("http", out _serverCertificate, out _serverCertificateChain))
            {
                throw new InvalidOperationException("The Scheduler https endpoint 'http' requires its certificate Secret mount (default 'tls').");
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
        pipeline.Use(next => context => InvokeAsync(
            controlPlane,
            _bootstrapVerifier,
            _resourceAudience,
            applicationContext,
            context,
            next));
    }

    public ServiceId Id { get; } = ServiceId.New();

    public Task StartAsync(CancellationToken cancellationToken = default) =>
        ((IHost)_application).StartAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken = default) =>
        ((IHost)_application).StopAsync(cancellationToken);

    public void Dispose()
    {
        ((IDisposable)_application).Dispose();
        _serverCertificate?.Dispose();
        foreach (X509Certificate2 certificate in _serverCertificateChain)
        {
            certificate.Dispose();
        }
        _bootstrapVerifier.Dispose();
    }

    private static async Task InvokeAsync(
        IResourceControlPlane controlPlane,
        BootstrapTokenVerifier bootstrapVerifier,
        string resourceAudience,
        SchedulerApplicationContext applicationContext,
        IHttpContext context,
        WebApplicationMiddleware next)
    {
        string path = context.Request.Path.Value;
        bool namespaced = path == "/cohesion/v1" ||
            path.StartsWith("/cohesion/v1/", StringComparison.Ordinal);
        BootstrapTokenStatus authorization = Authorize(
            context,
            bootstrapVerifier,
            resourceAudience);
        if (namespaced && authorization is not BootstrapTokenStatus.Authorized)
        {
            context.Response.StatusCode = authorization is BootstrapTokenStatus.Forbidden
                ? CohesionHttpStatusCode.Forbidden
                : CohesionHttpStatusCode.Unauthorized;
            if (authorization is BootstrapTokenStatus.Unauthorized)
            {
                context.Response.Headers[HttpHeaderKey.WWWAuthenticate] = "Bearer";
            }

            return;
        }

        if (path is "/healthz" or "/cohesion/v1/healthz")
        {
            await HandleHealthAsync(controlPlane, applicationContext, context, HealthKind.Health)
                .ConfigureAwait(false);
        }
        else if (path is "/readyz" or "/cohesion/v1/readyz")
        {
            await HandleHealthAsync(controlPlane, applicationContext, context, HealthKind.Readiness)
                .ConfigureAwait(false);
        }
        else if (path is "/livez" or "/cohesion/v1/livez")
        {
            await HandleHealthAsync(controlPlane, applicationContext, context, HealthKind.Liveness)
                .ConfigureAwait(false);
        }
        else if (path == "/cohesion/v1/endpoints")
        {
            if (!RequireRead(context))
            {
                return;
            }

            await WriteJsonAsync(context, writer =>
            {
                writer.WriteStartObject();
                writer.WritePropertyName("endpoints");
                writer.WriteStartObject();
                foreach ((string name, Uri address) in controlPlane.ObservedEndpoints)
                {
                    writer.WriteString(name, address.ToEndpointString());
                }

                writer.WriteEndObject();
                writer.WriteEndObject();
            }).ConfigureAwait(false);
        }
        else if (path == "/cohesion/v1/commands")
        {
            if (!RequireRead(context))
            {
                return;
            }

            await WriteJsonAsync(context, writer =>
            {
                writer.WriteStartObject();
                writer.WritePropertyName("acceptedCommandKinds");
                writer.WriteStartArray();
                foreach (string kind in controlPlane.AcceptedCommandKinds)
                {
                    writer.WriteStringValue(kind);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }).ConfigureAwait(false);
        }
        else if (path == "/cohesion/v1/stop")
        {
            if (context.Request.Method != HttpMethod.Post)
            {
                SetMethodNotAllowed(context, "POST");
                return;
            }

            await controlPlane.RequestStopAsync(context.RequestCancelled).ConfigureAwait(false);
            context.Response.StatusCode = CohesionHttpStatusCode.Accepted;
        }
        else if (namespaced)
        {
            context.Response.StatusCode = CohesionHttpStatusCode.NotFound;
        }
        else
        {
            await next.Invoke(context).ConfigureAwait(false);
        }
    }

    private static async Task HandleHealthAsync(
        IResourceControlPlane controlPlane,
        SchedulerApplicationContext applicationContext,
        IHttpContext context,
        HealthKind kind)
    {
        if (!RequireRead(context))
        {
            return;
        }

        ResourceHealthReport report = kind switch
        {
            HealthKind.Health => await controlPlane.CheckHealthAsync(context.RequestCancelled).ConfigureAwait(false),
            HealthKind.Readiness => await controlPlane.CheckReadinessAsync(context.RequestCancelled).ConfigureAwait(false),
            _ => await controlPlane.CheckLivenessAsync(context.RequestCancelled).ConfigureAwait(false),
        };
        bool ready = kind is not HealthKind.Readiness || applicationContext.State is HostState.Started;
        HealthStatus status = ready ? report.Status : HealthStatus.Unhealthy;
        context.Response.StatusCode = status is HealthStatus.Unhealthy
            ? CohesionHttpStatusCode.ServiceUnavailable
            : CohesionHttpStatusCode.Ok;
        context.Response.Headers[HttpHeaderKey.CacheControl] = "no-store, no-cache";
        await WriteJsonAsync(context, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("status", status.ToString());
            writer.WriteEndObject();
        }, "application/health+json; charset=utf-8").ConfigureAwait(false);
    }

    private static bool RequireRead(IHttpContext context)
    {
        if (context.Request.Method == HttpMethod.Get || context.Request.Method == HttpMethod.Head)
        {
            return true;
        }

        SetMethodNotAllowed(context, "GET, HEAD");
        return false;
    }

    private static void SetMethodNotAllowed(IHttpContext context, string allow)
    {
        context.Response.StatusCode = CohesionHttpStatusCode.MethodNotAllowed;
        context.Response.Headers[HttpHeaderKey.Allow] = allow;
    }

    private static BootstrapTokenStatus Authorize(
        IHttpContext context,
        BootstrapTokenVerifier bootstrapVerifier,
        string resourceAudience)
    {
        if (!context.Request.Headers.TryGetValue(HttpHeaderKey.Authorization, out HttpHeaderValue authorization) ||
            !authorization.Value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return BootstrapTokenStatus.Unauthorized;
        }

        return bootstrapVerifier.Validate(
            authorization.Value["Bearer ".Length..],
            resourceAudience,
            DateTimeOffset.UtcNow);
    }

    private static async Task WriteJsonAsync(
        IHttpContext context,
        Action<Utf8JsonWriter> write,
        string contentType = "application/json; charset=utf-8")
    {
        context.Response.Headers[HttpHeaderKey.ContentType] = contentType;
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            write.Invoke(writer);
        }

        if (context.Request.Method != HttpMethod.Head)
        {
            await context.Response.Body.WriteAsync(buffer.WrittenMemory, context.RequestCancelled)
                .ConfigureAwait(false);
        }
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
                $"The ambient Scheduler endpoint host '{host}' is not a bindable IP address.");
    }

    private enum HealthKind
    {
        Health,
        Readiness,
        Liveness,
    }
}
