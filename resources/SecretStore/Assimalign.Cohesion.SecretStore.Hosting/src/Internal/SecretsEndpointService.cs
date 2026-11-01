using System;
using System.Buffers;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections.Security;
using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Http.Connections;
using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.Hosting;

using HttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;

namespace Assimalign.Cohesion.SecretStore.Hosting.Internal;

[global::System.Diagnostics.DebuggerDisplay("SecretsEndpointService {Id,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class SecretsEndpointService : IHostService, IDisposable
{
    // The name the store's own certificate authority issues the HTTPS listener's certificate for.
    private const string ServerCertificateName = "secret-store";

    private readonly SecretStoreApplicationContext _applicationContext;
    private readonly CertificateAuthorityManager _certificateAuthority;
    private readonly Uri _endpoint;
    private readonly SecretStoreRepository _repository;
    private readonly string _basePath;
    private WebApplication? _host;
    private bool _isDisposed;
    private X509Certificate2? _serverCertificate;
    private SslStreamCertificateContext? _serverCertificateContext;

    internal SecretsEndpointService(
        Uri endpoint,
        SecretStoreRepository repository,
        CertificateAuthorityManager certificateAuthority,
        SecretStoreApplicationContext applicationContext)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(certificateAuthority);
        ArgumentNullException.ThrowIfNull(applicationContext);

        if (!string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The SecretStore 'api' endpoint must use HTTP or HTTPS, not '{endpoint.Scheme}'.");
        }

        _endpoint = endpoint;
        _repository = repository;
        _certificateAuthority = certificateAuthority;
        _applicationContext = applicationContext;

        IPAddress bindAddress = ResolveBindAddress(endpoint.IdnHost);
        if (!IPAddress.IsLoopback(bindAddress))
        {
            throw new InvalidOperationException(
                "A SecretStore takes no credentials, so it must bind to loopback.");
        }

        if (string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(applicationContext.Environment.Name, AppEnvironment.Keys.Local, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "A SecretStore permits plaintext HTTP only on loopback in Local.");
        }

        _basePath = endpoint.AbsolutePath == "/"
            ? string.Empty
            : endpoint.AbsolutePath.TrimEnd('/');
    }

    public ServiceId Id { get; } = ServiceId.New();

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        await _repository.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _certificateAuthority.InitializeAsync(cancellationToken).ConfigureAwait(false);
        ProtectedFileStore.HardenKeyDirectory(_repository.DataPath);
        ProtectedFileStore.HardenKeyDirectory(Path.Combine(_repository.DataPath, "key-ring"));

        IPAddress address = ResolveBindAddress(_endpoint.IdnHost);
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        if (string.Equals(_endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            _serverCertificateContext = await _certificateAuthority.GetServerCertificateContextAsync(
                    ServerCertificateName,
                    _endpoint.IdnHost,
                    cancellationToken)
                .ConfigureAwait(false);
            _serverCertificate = _serverCertificateContext.TargetCertificate;
            builder.Server.UseServer((HttpConnectionListenerOptions options) => options.UseHttp1s(
                tcp => tcp.EndPoint = new IPEndPoint(address, _endpoint.Port),
                new TlsServerOptions
                {
                    AuthenticationOptions = new SslServerAuthenticationOptions
                    {
                        ServerCertificateContext = _serverCertificateContext,
                    },
                }));
        }
        else
        {
            builder.Server.UseServer((HttpConnectionListenerOptions options) =>
                options.UseHttp1(tcp => tcp.EndPoint = new IPEndPoint(address, _endpoint.Port)));
        }

        _host = builder.Build();
        IWebApplicationPipelineBuilder pipeline = _host;
        pipeline.Use(next => context => InvokeAsync(context, next));

        await ((IHost)_host).StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        WebApplication? host = _host;
        _host = null;
        try
        {
            if (host is not null)
            {
                await ((IHost)host).StopAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (host is not null)
            {
                ((IDisposable)host).Dispose();
            }

            _serverCertificate?.Dispose();
            _serverCertificate = null;
            _serverCertificateContext = null;
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        if (_host is not null)
        {
            ((IDisposable)_host).Dispose();
            _host = null;
        }

        _serverCertificate?.Dispose();
        _serverCertificate = null;
        _serverCertificateContext = null;

        // The certificate authority is injected: the application's service provider created it and
        // disposes it after this service.
    }

    internal async Task InvokeAsync(IHttpContext context, WebApplicationMiddleware next)
    {
        string path = context.Request.Path.Value;
        bool isNamespaced = path == Route("/cohesion/v1") ||
            path.StartsWith(Route("/cohesion/v1/"), StringComparison.Ordinal);

        if (isNamespaced && !IsLoopbackPeer(context))
        {
            context.Response.StatusCode = HttpStatusCode.Forbidden;
            return;
        }

        if (path == Route("/healthz") || path == Route("/cohesion/v1/healthz"))
        {
            await HandleHealthAsync(context, HealthReportKind.Health).ConfigureAwait(false);
            return;
        }

        if (path == Route("/readyz") || path == Route("/cohesion/v1/readyz"))
        {
            await HandleHealthAsync(context, HealthReportKind.Readiness).ConfigureAwait(false);
            return;
        }

        if (path == Route("/livez") || path == Route("/cohesion/v1/livez"))
        {
            await HandleHealthAsync(context, HealthReportKind.Liveness).ConfigureAwait(false);
            return;
        }

        if (path == Route("/cohesion/v1/endpoints"))
        {
            await HandleEndpointsAsync(context).ConfigureAwait(false);
            return;
        }

        if (path == Route("/cohesion/v1/secrets"))
        {
            await HandleSecretAsync(context).ConfigureAwait(false);
            return;
        }

        if (path == Route("/cohesion/v1/certificates"))
        {
            await HandleCertificateAsync(context).ConfigureAwait(false);
            return;
        }

        if (isNamespaced)
        {
            context.Response.StatusCode = HttpStatusCode.NotFound;
            return;
        }

        await next.Invoke(context).ConfigureAwait(false);
    }

    private async Task HandleSecretAsync(IHttpContext context)
    {
        if (!IsRead(context))
        {
            SetMethodNotAllowed(context, "GET, HEAD");
            return;
        }

        if (!TryGetQuery(context, "path", out string? path))
        {
            context.Response.StatusCode = HttpStatusCode.BadRequest;
            return;
        }

        ReadOnlyMemory<byte>? value = await _repository.ReadAsync(path!, context.RequestCancelled).ConfigureAwait(false);
        if (value is null)
        {
            context.Response.StatusCode = HttpStatusCode.NotFound;
            return;
        }

        context.Response.Headers[HttpHeaderKey.CacheControl] = "no-store, no-cache";
        await WriteBytesAsync(context, value.Value, "application/octet-stream").ConfigureAwait(false);
    }

    private async Task HandleCertificateAsync(IHttpContext context)
    {
        if (!IsRead(context))
        {
            SetMethodNotAllowed(context, "GET, HEAD");
            return;
        }

        if (!TryGetQuery(context, "name", out string? name))
        {
            context.Response.StatusCode = HttpStatusCode.BadRequest;
            return;
        }

        try
        {
            string pem = await _certificateAuthority
                .GetCertificatePemAsync(name!, context.RequestCancelled)
                .ConfigureAwait(false);
            byte[] encoded = System.Text.Encoding.UTF8.GetBytes(pem);
            try
            {
                context.Response.Headers[HttpHeaderKey.CacheControl] = "no-store, no-cache";
                await WriteBytesAsync(
                        context,
                        encoded,
                        "application/x-pem-file")
                    .ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(encoded);
            }
        }
        catch (ArgumentException)
        {
            context.Response.StatusCode = HttpStatusCode.BadRequest;
        }
        catch (NotSupportedException)
        {
            context.Response.StatusCode = HttpStatusCode.NotImplemented;
        }
    }

    private async Task HandleEndpointsAsync(IHttpContext context)
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
            writer.WriteEndObject();
            writer.WriteEndObject();
        }, "application/health+json; charset=utf-8").ConfigureAwait(false);
    }

    // The namespaced routes take no credentials: only a loopback transport peer is accepted, and
    // forwarded headers never establish locality.
    private static bool IsLoopbackPeer(IHttpContext context)
    {
        if (context.ConnectionInfo.RemoteEndPoint is not IPEndPoint remote)
        {
            return false;
        }

        IPAddress address = remote.Address.IsIPv4MappedToIPv6
            ? remote.Address.MapToIPv4()
            : remote.Address;
        return address.Equals(IPAddress.Loopback) || address.Equals(IPAddress.IPv6Loopback);
    }

    private string Route(string path) => _basePath + path;

    private static bool IsRead(IHttpContext context)
        => context.Request.Method == HttpMethod.Get || context.Request.Method == HttpMethod.Head;

    private static bool TryGetQuery(IHttpContext context, string name, out string? value)
    {
        if (context.Request.Query.TryGetValue(name, out HttpQueryValue query) &&
            !string.IsNullOrWhiteSpace(query.Value))
        {
            value = query.Value;
            return true;
        }

        value = null;
        return false;
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

    private static async Task WriteBytesAsync(
        IHttpContext context,
        ReadOnlyMemory<byte> bytes,
        string contentType)
    {
        context.Response.Headers[HttpHeaderKey.ContentType] = contentType;
        if (context.Request.Method != HttpMethod.Head)
        {
            await context.Response.Body.WriteAsync(bytes, context.RequestCancelled).ConfigureAwait(false);
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
                $"The SecretStore endpoint host '{host}' is not a bindable IP address.");
    }

    private enum HealthReportKind
    {
        Health,
        Readiness,
        Liveness,
    }

    private sealed class DebugView
    {
        private readonly SecretsEndpointService _target;

        public DebugView(SecretsEndpointService value)
        {
            _target = value;
        }

        public ServiceId Id => _target.Id;
    }
}
