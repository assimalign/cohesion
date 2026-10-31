using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.Hosting;

using HttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;

namespace Assimalign.Cohesion.ConfigurationStore.Hosting.Internal;

[global::System.Diagnostics.DebuggerDisplay("ConfigurationEndpointService {Id,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class ConfigurationEndpointService : IHostService, IDisposable
{
    private readonly ConfigurationStoreApplicationContext _applicationContext;
    private readonly string _basePath;
    private readonly Uri _endpoint;
    private readonly WebApplication _host;
    private readonly ConfigurationStoreRepository _repository;

    internal ConfigurationEndpointService(
        Uri endpoint,
        ConfigurationStoreRepository repository,
        ConfigurationStoreApplicationContext applicationContext)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(applicationContext);

        if (!string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The ConfigurationStore 'api' endpoint must use http, not '{endpoint.Scheme}'.");
        }

        _endpoint = endpoint;
        _repository = repository;
        _applicationContext = applicationContext;
        _basePath = endpoint.AbsolutePath == "/"
            ? string.Empty
            : endpoint.AbsolutePath.TrimEnd('/');

        IPAddress address = ResolveBindAddress(endpoint.IdnHost);
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Server.UseServer(options => options.UseHttp1(tcp => tcp.EndPoint = new IPEndPoint(address, endpoint.Port)));

        _host = builder.Build();
        IWebApplicationPipelineBuilder pipeline = _host;
        pipeline.Use(next => context => InvokeAsync(context, next));
    }

    public ServiceId Id { get; } = ServiceId.New();

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _repository.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await ((IHost)_host).StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        return ((IHost)_host).StopAsync(cancellationToken);
    }

    public void Dispose()
    {
        ((IDisposable)_host).Dispose();
    }

    internal async Task InvokeAsync(IHttpContext context, WebApplicationMiddleware next)
    {
        string path = context.Request.Path.Value;
        string healthPath = Route("/healthz");
        string readinessPath = Route("/readyz");
        string livenessPath = Route("/livez");
        string cohesionHealthPath = Route("/cohesion/v1/healthz");
        string cohesionReadinessPath = Route("/cohesion/v1/readyz");
        string cohesionLivenessPath = Route("/cohesion/v1/livez");
        bool isNamespaced = path == Route("/cohesion/v1") ||
            path.StartsWith(Route("/cohesion/v1/"), StringComparison.Ordinal);

        if (isNamespaced && !IsLoopbackPeer(context))
        {
            context.Response.StatusCode = HttpStatusCode.Forbidden;
            return;
        }

        if (path == healthPath || path == cohesionHealthPath)
        {
            await HandleHealthAsync(context, HealthReportKind.Health).ConfigureAwait(false);
            return;
        }

        if (path == readinessPath || path == cohesionReadinessPath)
        {
            await HandleHealthAsync(context, HealthReportKind.Readiness).ConfigureAwait(false);
            return;
        }

        if (path == livenessPath || path == cohesionLivenessPath)
        {
            await HandleHealthAsync(context, HealthReportKind.Liveness).ConfigureAwait(false);
            return;
        }

        if (path == Route("/cohesion/v1/endpoints"))
        {
            if (!IsRead(context))
            {
                SetMethodNotAllowed(context, "GET, HEAD");
                return;
            }

            await WriteJsonAsync(context, writer =>
            {
                writer.WriteStartObject();
                writer.WritePropertyName("endpoints");
                writer.WriteStartObject();
                writer.WriteString("api", _endpoint.ToEndpointString());
                writer.WriteEndObject();
                writer.WriteEndObject();
            }).ConfigureAwait(false);
            return;
        }

        if (path == Route("/cohesion/v1/namespaces"))
        {
            await HandleNamespacesAsync(context).ConfigureAwait(false);
            return;
        }

        if (isNamespaced)
        {
            context.Response.StatusCode = HttpStatusCode.NotFound;
            return;
        }

        await next.Invoke(context).ConfigureAwait(false);
    }

    private async Task HandleNamespacesAsync(IHttpContext context)
    {
        if (!IsRead(context))
        {
            SetMethodNotAllowed(context, "GET, HEAD");
            return;
        }

        if (!context.Request.Query.TryGetValue("name", out HttpQueryValue nameValue))
        {
            IReadOnlyList<string> names = await _repository
                .ListAsync(context.RequestCancelled)
                .ConfigureAwait(false);
            await WriteJsonAsync(context, writer =>
            {
                writer.WriteStartArray();
                for (int index = 0; index < names.Count; index++)
                {
                    writer.WriteStringValue(names[index]);
                }

                writer.WriteEndArray();
            }).ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrWhiteSpace(nameValue.Value))
        {
            context.Response.StatusCode = HttpStatusCode.BadRequest;
            return;
        }

        IReadOnlyDictionary<string, string?>? values = await _repository
            .ReadAsync(nameValue.Value, context.RequestCancelled)
            .ConfigureAwait(false);
        if (values is null)
        {
            context.Response.StatusCode = HttpStatusCode.NotFound;
            return;
        }

        await WriteJsonAsync(context, writer =>
        {
            writer.WriteStartObject();
            foreach ((string key, string? value) in
                values.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            {
                if (value is null)
                {
                    writer.WriteNull(key);
                }
                else
                {
                    writer.WriteString(key, value);
                }
            }

            writer.WriteEndObject();
        }).ConfigureAwait(false);
    }

    private async Task HandleHealthAsync(IHttpContext context, HealthReportKind kind)
    {
        if (!IsRead(context))
        {
            SetMethodNotAllowed(context, "GET, HEAD");
            return;
        }

        bool ready = kind is not HealthReportKind.Readiness ||
            _applicationContext.State is HostState.Started;
        context.Response.StatusCode = ready
            ? HttpStatusCode.Ok
            : HttpStatusCode.ServiceUnavailable;
        context.Response.Headers[HttpHeaderKey.CacheControl] = "no-store, no-cache";
        await WriteJsonAsync(context, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("status", ready ? "Healthy" : "Unhealthy");
            writer.WritePropertyName("contributions");
            writer.WriteStartObject();
            if (!ready)
            {
                writer.WritePropertyName("cohesion.host");
                writer.WriteStartObject();
                writer.WriteString("status", "Unhealthy");
                writer.WriteString("description", "The ConfigurationStore host has not completed startup.");
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }, "application/health+json; charset=utf-8").ConfigureAwait(false);
    }

    // The namespaced routes take no credentials: only a loopback transport peer is accepted, and
    // forwarded headers never establish locality.
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

    private string Route(string path) => _basePath + path;

    private static bool IsRead(IHttpContext context)
    {
        return context.Request.Method == HttpMethod.Get || context.Request.Method == HttpMethod.Head;
    }

    private static void SetMethodNotAllowed(IHttpContext context, string allow)
    {
        context.Response.StatusCode = HttpStatusCode.MethodNotAllowed;
        context.Response.Headers[HttpHeaderKey.Allow] = allow;
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
            await context.Response.Body
                .WriteAsync(buffer.WrittenMemory, context.RequestCancelled)
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
                $"The ConfigurationStore endpoint host '{host}' is not a bindable IP address.");
    }

    private enum HealthReportKind
    {
        Health,
        Readiness,
        Liveness,
    }

    private sealed class DebugView
    {
        private readonly ConfigurationEndpointService _target;

        public DebugView(ConfigurationEndpointService value)
        {
            _target = value;
        }

        public ServiceId Id => _target.Id;
    }
}
