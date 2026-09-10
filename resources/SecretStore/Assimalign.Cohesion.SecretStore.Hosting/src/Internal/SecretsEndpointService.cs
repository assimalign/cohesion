using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections.Security;
using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Health;
using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Http.Connections;
using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.Hosting;

using HttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;

namespace Assimalign.Cohesion.SecretStore.Hosting;

internal sealed class SecretsEndpointService : IHostService, IDisposable
{
    internal const string TrustAddCommand = "cohesion.trust.add";

    private readonly SecretStoreApplicationContext _applicationContext;
    private readonly string _audience;
    private readonly CertificateAuthorityManager _certificateAuthority;
    private readonly IResourceControlPlane? _controlPlane;
    private readonly Uri _endpoint;
    private readonly bool _requireAuthentication;
    private readonly SecretStoreRepository _repository;
    private readonly ResourceContext _resourceContext;
    private readonly TrustedIssuerStore _trustedIssuers;
    private readonly string _basePath;
    private WebApplication? _host;
    private bool _isDisposed;
    private X509Certificate2? _serverCertificate;
    private SslStreamCertificateContext? _serverCertificateContext;
    private BootstrapTokenVerifier? _tokenVerifier;

    internal SecretsEndpointService(
        Uri endpoint,
        SecretStoreRepository repository,
        TrustedIssuerStore trustedIssuers,
        CertificateAuthorityManager certificateAuthority,
        IResourceControlPlane? controlPlane,
        ResourceContext resourceContext,
        SecretStoreApplicationContext applicationContext)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(trustedIssuers);
        ArgumentNullException.ThrowIfNull(certificateAuthority);
        ArgumentNullException.ThrowIfNull(resourceContext);
        ArgumentNullException.ThrowIfNull(applicationContext);

        if (!string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The SecretStore 'api' endpoint must use HTTP or HTTPS, not '{endpoint.Scheme}'.");
        }

        _endpoint = endpoint;
        _repository = repository;
        _trustedIssuers = trustedIssuers;
        _certificateAuthority = certificateAuthority;
        _controlPlane = controlPlane;
        _resourceContext = resourceContext;
        _applicationContext = applicationContext;
        _requireAuthentication = resourceContext.GatewayName is not null;
        _audience = resourceContext.ResourceName ?? string.Empty;
        if (_requireAuthentication && string.IsNullOrWhiteSpace(_audience))
        {
            throw new InvalidOperationException(
                "A gateway-managed SecretStore requires an ambient resource name.");
        }

        IPAddress bindAddress = ResolveBindAddress(endpoint.IdnHost);
        if (!_requireAuthentication && !IPAddress.IsLoopback(bindAddress))
        {
            throw new InvalidOperationException(
                "A standalone SecretStore without bootstrap authentication must bind to loopback.");
        }

        if (string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            (!string.Equals(resourceContext.EnvironmentName, "Development", StringComparison.OrdinalIgnoreCase) ||
             !IPAddress.IsLoopback(bindAddress)))
        {
            throw new InvalidOperationException(
                "A SecretStore permits plaintext HTTP only on loopback in Development.");
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
        await _trustedIssuers.InitializeAsync(
                _resourceContext,
                _requireAuthentication,
                cancellationToken)
            .ConfigureAwait(false);
        _tokenVerifier = new BootstrapTokenVerifier(_trustedIssuers);
        await _certificateAuthority.InitializeAsync(cancellationToken).ConfigureAwait(false);
        ProtectedFileStore.HardenKeyDirectory(_repository.DataPath);
        ProtectedFileStore.HardenKeyDirectory(Path.Combine(_repository.DataPath, "key-ring"));

        IPAddress address = ResolveBindAddress(_endpoint.IdnHost);
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        if (string.Equals(_endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            _serverCertificateContext = await _certificateAuthority.GetServerCertificateContextAsync(
                    _resourceContext.ResourceName ?? "secret-store",
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

        _controlPlane?.ObserveEndpoint("api", _endpoint);
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
        _certificateAuthority.Dispose();
    }

    private async Task InvokeAsync(IHttpContext context, WebApplicationMiddleware next)
    {
        string path = context.Request.Path.Value;
        bool isNamespaced = path == Route("/cohesion/v1") ||
            path.StartsWith(Route("/cohesion/v1/"), StringComparison.Ordinal);

        BootstrapTokenValidation authorization = new(
            BootstrapTokenValidationStatus.Authorized,
            null,
            null);
        if (isNamespaced)
        {
            authorization = Authorize(context);
            if (authorization.Status is not BootstrapTokenValidationStatus.Authorized)
            {
                SetAuthorizationFailure(context, authorization.Status);
                return;
            }

            bool acceptsPeerIssuer = path == Route("/cohesion/v1/certificates/enroll");
            if (_requireAuthentication &&
                !acceptsPeerIssuer &&
                !string.Equals(
                    authorization.Issuer,
                    _resourceContext.ApplicationName,
                    StringComparison.Ordinal))
            {
                SetAuthorizationFailure(context, BootstrapTokenValidationStatus.Forbidden);
                return;
            }
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

        if (path == Route("/cohesion/v1/stop"))
        {
            await HandleStopAsync(context).ConfigureAwait(false);
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

        if (path == Route("/cohesion/v1/certificates/enrollment-request"))
        {
            await HandleEnrollmentRequestAsync(context).ConfigureAwait(false);
            return;
        }

        if (path == Route("/cohesion/v1/certificates/enroll"))
        {
            await HandleEnrollmentAsync(context, authorization).ConfigureAwait(false);
            return;
        }

        if (path == Route("/cohesion/v1/certificates/enrollment"))
        {
            await HandleEnrollmentCompletionAsync(context, authorization).ConfigureAwait(false);
            return;
        }

        if (path == Route("/cohesion/v1/commands"))
        {
            await HandleCommandsAsync(context, authorization).ConfigureAwait(false);
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

        ReadOnlyMemory<byte>? value = string.Equals(path, "trusted-issuers.json", StringComparison.Ordinal)
            ? _trustedIssuers.Export()
            : await _repository.ReadAsync(path!, context.RequestCancelled).ConfigureAwait(false);
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

    private async Task HandleCommandsAsync(
        IHttpContext context,
        BootstrapTokenValidation authorization)
    {
        if (IsRead(context))
        {
            await WriteJsonAsync(context, writer =>
            {
                writer.WriteStartObject();
                writer.WritePropertyName("acceptedCommandKinds");
                writer.WriteStartArray();
                writer.WriteStringValue(TrustAddCommand);
                writer.WriteEndArray();
                writer.WriteEndObject();
            }).ConfigureAwait(false);
            return;
        }

        if (context.Request.Method != HttpMethod.Post)
        {
            SetMethodNotAllowed(context, "GET, HEAD, POST");
            return;
        }

        SecretStoreCommand command;
        try
        {
            command = await ReadCommandAsync(context).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JsonException or FormatException)
        {
            context.Response.StatusCode = HttpStatusCode.BadRequest;
            return;
        }

        string? authenticatedOwner = authorization.Issuer is null || authorization.Subject is null
            ? null
            : authorization.Issuer + "@" + authorization.Subject;
        if (_requireAuthentication &&
            !string.Equals(command.Owner, authenticatedOwner, StringComparison.Ordinal))
        {
            context.Response.StatusCode = HttpStatusCode.Forbidden;
            return;
        }

        if (!string.Equals(command.Kind, TrustAddCommand, StringComparison.Ordinal))
        {
            context.Response.StatusCode = HttpStatusCode.NotImplemented;
            return;
        }

        try
        {
            TrustedIssuerUpsertResult result = await _trustedIssuers.UpsertAsync(
                    command.Owner,
                    command.Key,
                    command.Payload,
                    context.RequestCancelled)
                .ConfigureAwait(false);
            context.Response.StatusCode = result is TrustedIssuerUpsertResult.Stored
                ? HttpStatusCode.NoContent
                : HttpStatusCode.Conflict;
        }
        catch (InvalidDataException)
        {
            context.Response.StatusCode = HttpStatusCode.BadRequest;
        }
    }

    private async Task HandleEnrollmentRequestAsync(IHttpContext context)
    {
        if (!IsRead(context))
        {
            SetMethodNotAllowed(context, "GET, HEAD");
            return;
        }

        if (!TryGetQuery(context, "application", out string? application) ||
            !TryGetQuery(context, "resource", out string? resource))
        {
            context.Response.StatusCode = HttpStatusCode.BadRequest;
            return;
        }

        if (!string.Equals(
                application,
                _resourceContext.ApplicationName,
                StringComparison.Ordinal) ||
            !string.Equals(
                resource,
                _resourceContext.ResourceName,
                StringComparison.Ordinal))
        {
            context.Response.StatusCode = _requireAuthentication
                ? HttpStatusCode.Forbidden
                : HttpStatusCode.BadRequest;
            return;
        }

        try
        {
            string csr = await _certificateAuthority.CreateEnrollmentRequestAsync(
                    application!,
                    resource!,
                    context.RequestCancelled)
                .ConfigureAwait(false);
            await WriteJsonAsync(context, writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("application", application);
                writer.WriteString("resource", resource);
                writer.WriteString("certificateSigningRequest", csr);
                writer.WriteEndObject();
            }).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            context.Response.StatusCode = HttpStatusCode.Conflict;
        }
    }

    private async Task HandleEnrollmentAsync(
        IHttpContext context,
        BootstrapTokenValidation authorization)
    {
        if (context.Request.Method != HttpMethod.Post)
        {
            SetMethodNotAllowed(context, "POST");
            return;
        }

        try
        {
            using JsonDocument document = await ReadJsonAsync(context).ConfigureAwait(false);
            JsonElement root = document.RootElement;
            string application = GetRequiredString(root, "application");
            string resource = GetRequiredString(root, "resource");
            string csr = GetRequiredString(root, "certificateSigningRequest");
            if (_requireAuthentication &&
                !string.Equals(application, authorization.Issuer, StringComparison.Ordinal))
            {
                context.Response.StatusCode = HttpStatusCode.Forbidden;
                return;
            }

            CertificateEnrollmentResponse response = await _certificateAuthority
                .IssueIntermediateAsync(
                    application,
                    resource,
                    csr,
                    context.RequestCancelled)
                .ConfigureAwait(false);
            await WriteJsonAsync(context, writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("certificate", response.Certificate);
                writer.WritePropertyName("issuerChain");
                writer.WriteStartArray();
                for (int index = 0; index < response.IssuerChain.Count; index++)
                {
                    writer.WriteStringValue(response.IssuerChain[index]);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is JsonException or FormatException or InvalidDataException or CryptographicException)
        {
            context.Response.StatusCode = HttpStatusCode.BadRequest;
        }
    }

    private async Task HandleEnrollmentCompletionAsync(
        IHttpContext context,
        BootstrapTokenValidation authorization)
    {
        if (context.Request.Method != HttpMethod.Post)
        {
            SetMethodNotAllowed(context, "POST");
            return;
        }

        if (_requireAuthentication &&
            !string.Equals(_resourceContext.ApplicationName, authorization.Issuer, StringComparison.Ordinal))
        {
            context.Response.StatusCode = HttpStatusCode.Forbidden;
            return;
        }

        try
        {
            using JsonDocument document = await ReadJsonAsync(context).ConfigureAwait(false);
            JsonElement root = document.RootElement;
            string certificate = GetRequiredString(root, "certificate");
            if (!root.TryGetProperty("issuerChain", out JsonElement chainProperty) ||
                chainProperty.ValueKind is not JsonValueKind.Array)
            {
                throw new JsonException("An enrollment completion requires an issuerChain array.");
            }

            string[] chain = chainProperty.EnumerateArray()
                .Select(static item => item.GetString() ?? throw new JsonException(
                    "Every issuerChain member must be a string."))
                .ToArray();
            await _certificateAuthority.CompleteEnrollmentAsync(
                    certificate,
                    chain,
                    context.RequestCancelled)
                .ConfigureAwait(false);
            context.Response.StatusCode = HttpStatusCode.NoContent;
        }
        catch (Exception exception) when (
            exception is JsonException or InvalidDataException or CryptographicException)
        {
            context.Response.StatusCode = HttpStatusCode.BadRequest;
        }
        catch (InvalidOperationException)
        {
            context.Response.StatusCode = HttpStatusCode.Conflict;
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
            IReadOnlyDictionary<string, Uri> endpoints =
                _controlPlane?.ObservedEndpoints ?? new Dictionary<string, Uri>
                {
                    ["api"] = _endpoint,
                };
            foreach ((string name, Uri endpoint) in
                endpoints.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            {
                writer.WriteString(name, endpoint.ToEndpointString());
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }).ConfigureAwait(false);
    }

    private async Task HandleStopAsync(IHttpContext context)
    {
        if (context.Request.Method != HttpMethod.Post)
        {
            SetMethodNotAllowed(context, "POST");
            return;
        }

        if (_controlPlane is null)
        {
            context.Response.StatusCode = HttpStatusCode.NotFound;
            return;
        }

        await _controlPlane.RequestStopAsync(context.RequestCancelled).ConfigureAwait(false);
        context.Response.StatusCode = HttpStatusCode.Accepted;
    }

    private async Task HandleHealthAsync(IHttpContext context, HealthReportKind kind)
    {
        if (!IsRead(context))
        {
            SetMethodNotAllowed(context, "GET, HEAD");
            return;
        }

        ResourceHealthReport report = _controlPlane is null
            ? new ResourceHealthReport(
                HealthStatus.Healthy,
                new Dictionary<string, HealthContribution>())
            : kind switch
            {
                HealthReportKind.Health => await _controlPlane
                    .CheckHealthAsync(context.RequestCancelled)
                    .ConfigureAwait(false),
                HealthReportKind.Readiness => await _controlPlane
                    .CheckReadinessAsync(context.RequestCancelled)
                    .ConfigureAwait(false),
                _ => await _controlPlane
                    .CheckLivenessAsync(context.RequestCancelled)
                    .ConfigureAwait(false),
            };
        bool ready = kind is not HealthReportKind.Readiness ||
            (_applicationContext.State is HostState.Started && _certificateAuthority.IsEnrolled);
        context.Response.StatusCode = report.Status is HealthStatus.Unhealthy || !ready
            ? HttpStatusCode.ServiceUnavailable
            : HttpStatusCode.Ok;
        context.Response.Headers[HttpHeaderKey.CacheControl] = "no-store, no-cache";
        await WriteJsonAsync(context, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("status", ready ? report.Status.ToString() : HealthStatus.Unhealthy.ToString());
            writer.WritePropertyName("contributions");
            writer.WriteStartObject();
            foreach ((string name, HealthContribution contribution) in
                report.Contributions.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            {
                writer.WritePropertyName(name);
                writer.WriteStartObject();
                writer.WriteString("status", contribution.Status.ToString());
                if (contribution.Description is not null)
                {
                    writer.WriteString("description", contribution.Description);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }, "application/health+json; charset=utf-8").ConfigureAwait(false);
    }

    private BootstrapTokenValidation Authorize(IHttpContext context)
    {
        if (!_requireAuthentication)
        {
            return new BootstrapTokenValidation(
                BootstrapTokenValidationStatus.Authorized,
                null,
                null);
        }

        if (!context.Request.Headers.TryGetValue(
                HttpHeaderKey.Authorization,
                out HttpHeaderValue authorization) ||
            !authorization.Value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return BootstrapTokenValidation.Unauthorized;
        }

        string token = authorization.Value["Bearer ".Length..];
        return _tokenVerifier?.Validate(token, _audience, DateTimeOffset.UtcNow) ??
            BootstrapTokenValidation.Unauthorized;
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

    private static void SetAuthorizationFailure(
        IHttpContext context,
        BootstrapTokenValidationStatus status)
    {
        context.Response.StatusCode = status is BootstrapTokenValidationStatus.Forbidden
            ? HttpStatusCode.Forbidden
            : HttpStatusCode.Unauthorized;
        if (status is BootstrapTokenValidationStatus.Unauthorized)
        {
            context.Response.Headers[HttpHeaderKey.WWWAuthenticate] = "Bearer";
        }
    }

    private static void SetMethodNotAllowed(IHttpContext context, string allow)
    {
        context.Response.StatusCode = HttpStatusCode.MethodNotAllowed;
        context.Response.Headers[HttpHeaderKey.Allow] = allow;
    }

    private static async Task<SecretStoreCommand> ReadCommandAsync(IHttpContext context)
    {
        using JsonDocument document = await ReadJsonAsync(context).ConfigureAwait(false);
        JsonElement root = document.RootElement;
        byte[] payload = root.TryGetProperty("payload", out JsonElement payloadProperty)
            ? payloadProperty.GetBytesFromBase64()
            : [];
        return new SecretStoreCommand(
            GetRequiredString(root, "id"),
            GetRequiredString(root, "kind"),
            GetRequiredString(root, "owner"),
            GetRequiredString(root, "key"),
            payload);
    }

    private static async Task<JsonDocument> ReadJsonAsync(IHttpContext context)
    {
        JsonDocument document = await JsonDocument.ParseAsync(
                context.Request.Body,
                cancellationToken: context.RequestCancelled)
            .ConfigureAwait(false);
        if (document.RootElement.ValueKind is not JsonValueKind.Object)
        {
            document.Dispose();
            throw new JsonException("The request body must be a JSON object.");
        }

        return document;
    }

    private static string GetRequiredString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement property) ||
            property.ValueKind is not JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new JsonException($"Request property '{name}' is required.");
        }

        return property.GetString()!;
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

    private readonly record struct SecretStoreCommand(
        string Id,
        string Kind,
        string Owner,
        string Key,
        ReadOnlyMemory<byte> Payload);

    private enum HealthReportKind
    {
        Health,
        Readiness,
        Liveness,
    }
}
