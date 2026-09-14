using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
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

namespace Assimalign.Cohesion.IdentityHub.Hosting;

internal sealed class IdentityEndpointService : IHostService, IDisposable
{
    private const string DevelopmentDeviceSubject = "development-user";
    private const string DeviceGrant = "urn:ietf:params:oauth:grant-type:device_code";
    private const string OpenIdScope = "openid";
    private readonly bool _allowDevelopmentDeviceApproval;
    private readonly IdentityHubApplicationContext _applicationContext;
    private readonly IPAddress _bindAddress;
    private readonly IdentityCommandRegistry _registry;
    private readonly IResourceControlPlane _commands;
    private readonly string? _applicationIssuer;
    private readonly IResourceControlPlane? _controlPlane;
    private readonly string _dataPath;
    private readonly DeviceAuthorizationStore _devices = new();
    private readonly Uri _endpoint;
    private readonly string _issuer;
    private readonly string _basePath;
    private readonly bool _requireAuthentication;
    private readonly string _resourceAudience;
    private readonly BootstrapTokenVerifier? _bootstrapVerifier;
    private readonly ResourceContext _resourceContext;
    private IdentitySigningKey? _signingKey;
    private WebApplication? _host;
    private X509Certificate2? _serverCertificate;
    private X509Certificate2[] _serverCertificateChain = [];
    private SslStreamCertificateContext? _serverCertificateContext;

    internal IdentityEndpointService(
        Uri endpoint,
        string dataPath,
        IReadOnlyCollection<string> audiences,
        IReadOnlyDictionary<string, IdentityHubClientRegistration> clients,
        IResourceControlPlane? controlPlane,
        ResourceContext resourceContext,
        IdentityHubApplicationContext applicationContext)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataPath);
        ArgumentNullException.ThrowIfNull(audiences);
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(resourceContext);
        ArgumentNullException.ThrowIfNull(applicationContext);

        _endpoint = endpoint;
        _dataPath = dataPath;
        _registry = new IdentityCommandRegistry(dataPath, resourceContext, audiences, clients);
        _applicationIssuer = resourceContext.ApplicationName;
        _controlPlane = controlPlane;
        _commands = controlPlane ?? ResourceControlPlane.Create([IdentityResourceCommandHandler.AddAudience, IdentityResourceCommandHandler.AddClient]);
        foreach (string kind in _commands.AcceptedCommandKinds)
        {
            if (kind is IdentityResourceCommandHandler.AddAudience or IdentityResourceCommandHandler.AddClient)
            {
                _commands.RegisterCommandHandler(new IdentityResourceCommandHandler(kind, _registry));
            }
        }
        _applicationContext = applicationContext;
        _bindAddress = ResolveBindAddress(endpoint.IdnHost);
        _basePath = endpoint.AbsolutePath == "/" ? string.Empty : endpoint.AbsolutePath.TrimEnd('/');
        _issuer = endpoint.ToString().TrimEnd('/');
        _requireAuthentication = resourceContext.GatewayName is not null;
        _resourceAudience = resourceContext.ResourceName ?? string.Empty;
        _allowDevelopmentDeviceApproval =
            string.Equals(resourceContext.EnvironmentName, "Development", StringComparison.OrdinalIgnoreCase) &&
            IPAddress.IsLoopback(_bindAddress);
        _resourceContext = resourceContext;

        bool isHttp = string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);
        bool isHttps = string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        if (!isHttp && !isHttps)
        {
            throw new InvalidOperationException(
                $"The IdentityHub endpoint must use HTTP or HTTPS, not '{endpoint.Scheme}'.");
        }

        if (isHttp && !_allowDevelopmentDeviceApproval)
        {
            throw new InvalidOperationException(
                "IdentityHub permits plaintext HTTP only on loopback in Development.");
        }

        if (isHttps && resourceContext.TryGetEndpointCertificate("https", out _serverCertificate, out X509Certificate2Collection chain))
        {
            _serverCertificateChain = new X509Certificate2[chain.Count];
            chain.CopyTo(_serverCertificateChain, 0);
            _serverCertificateContext = SslStreamCertificateContext.Create(_serverCertificate, chain, offline: true);
        }

        if (isHttps && _serverCertificateContext is null && !_allowDevelopmentDeviceApproval)
        {
            throw new InvalidOperationException(
                "IdentityHub requires a PEM certificate, private key, and optional chain in the 'tls' " +
                "resource mount outside loopback Development. Self-signed TLS is development-only.");
        }

        if (_requireAuthentication)
        {
            if (string.IsNullOrWhiteSpace(_resourceAudience))
            {
                throw new InvalidOperationException(
                    "A gateway-managed IdentityHub requires an ambient resource name.");
            }

            _bootstrapVerifier = new BootstrapTokenVerifier(resourceContext);
        }
    }

    public ServiceId Id { get; } = ServiceId.New();

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        foreach (ResourceCommand command in await _registry.InitializeAsync(cancellationToken).ConfigureAwait(false))
        {
            await _commands.ExecuteCommandAsync(command, cancellationToken).ConfigureAwait(false);
        }
        _signingKey = await IdentitySigningKey.LoadOrCreateAsync(_dataPath, cancellationToken)
            .ConfigureAwait(false);
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        if (string.Equals(_endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            _serverCertificateContext ??= CreateDevelopmentServerCertificateContext(_endpoint.IdnHost);
            builder.Server.UseServer((HttpConnectionListenerOptions options) => options.UseHttp1s(
                tcp => tcp.EndPoint = new IPEndPoint(_bindAddress, _endpoint.Port),
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
                options.UseHttp1(tcp => tcp.EndPoint = new IPEndPoint(_bindAddress, _endpoint.Port)));
        }

        _host = builder.Build();
        IWebApplicationPipelineBuilder pipeline = _host;
        pipeline.Use(next => context => InvokeAsync(context, next));
        _controlPlane?.ObserveEndpoint("https", _endpoint);
        await ((IHost)_host).StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
        => _host is null ? Task.CompletedTask : ((IHost)_host).StopAsync(cancellationToken);

    public void Dispose()
    {
        if (_host is not null)
        {
            ((IDisposable)_host).Dispose();
        }

        _bootstrapVerifier?.Dispose();
        _signingKey?.Dispose();
        _serverCertificate?.Dispose();
        for (int index = 0; index < _serverCertificateChain.Length; index++)
        {
            _serverCertificateChain[index].Dispose();
        }
    }

    private async Task InvokeAsync(IHttpContext context, WebApplicationMiddleware next)
    {
        string path = context.Request.Path.Value;
        bool namespaced = path == Route("/cohesion/v1") ||
            path.StartsWith(Route("/cohesion/v1/"), StringComparison.Ordinal);
        if (namespaced)
        {
            BootstrapTokenStatus authorization = Authorize(context);
            if (authorization is not BootstrapTokenStatus.Authorized)
            {
                SetAuthorizationFailure(context, authorization);
                return;
            }
        }

        if (path == Route("/.well-known/openid-configuration"))
        {
            await HandleDiscoveryAsync(context).ConfigureAwait(false);
        }
        else if (path == Route("/oauth2/jwks"))
        {
            await HandleJwksAsync(context).ConfigureAwait(false);
        }
        else if (path == Route("/oauth2/token"))
        {
            await HandleTokenAsync(context).ConfigureAwait(false);
        }
        else if (path == Route("/oauth2/device_authorization"))
        {
            await HandleDeviceAuthorizationAsync(context).ConfigureAwait(false);
        }
        else if (path == Route("/oauth2/device"))
        {
            await HandleDeviceVerificationAsync(context).ConfigureAwait(false);
        }
        else if (path == Route("/healthz") || path == Route("/cohesion/v1/healthz"))
        {
            await HandleHealthAsync(context, HealthReportKind.Health).ConfigureAwait(false);
        }
        else if (path == Route("/readyz") || path == Route("/cohesion/v1/readyz"))
        {
            await HandleHealthAsync(context, HealthReportKind.Readiness).ConfigureAwait(false);
        }
        else if (path == Route("/livez") || path == Route("/cohesion/v1/livez"))
        {
            await HandleHealthAsync(context, HealthReportKind.Liveness).ConfigureAwait(false);
        }
        else if (path == Route("/cohesion/v1/endpoints"))
        {
            await HandleEndpointsAsync(context).ConfigureAwait(false);
        }
        else if (path == Route("/cohesion/v1/commands"))
        {
            await HandleCommandsAsync(context).ConfigureAwait(false);
        }
        else if (path == Route("/cohesion/v1/stop"))
        {
            await HandleStopAsync(context).ConfigureAwait(false);
        }
        else if (namespaced)
        {
            context.Response.StatusCode = HttpStatusCode.NotFound;
        }
        else
        {
            await next.Invoke(context).ConfigureAwait(false);
        }
    }

    private async Task HandleDiscoveryAsync(IHttpContext context)
    {
        if (!RequireRead(context))
        {
            return;
        }

        await WriteJsonAsync(context, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("issuer", _issuer);
            writer.WriteString("jwks_uri", Absolute("/oauth2/jwks"));
            writer.WriteString("token_endpoint", Absolute("/oauth2/token"));
            if (_allowDevelopmentDeviceApproval)
            {
                writer.WriteString(
                    "device_authorization_endpoint",
                    Absolute("/oauth2/device_authorization"));
            }

            WriteStringArray(
                writer,
                "grant_types_supported",
                _allowDevelopmentDeviceApproval
                    ? ["client_credentials", DeviceGrant]
                    : ["client_credentials"]);
            WriteStringArray(
                writer,
                "token_endpoint_auth_methods_supported",
                _allowDevelopmentDeviceApproval
                    ? ["client_secret_basic", "client_secret_post", "none"]
                    : ["client_secret_basic", "client_secret_post"]);
            WriteStringArray(writer, "id_token_signing_alg_values_supported", ["ES256"]);
            WriteStringArray(writer, "subject_types_supported", ["public"]);
            WriteStringArray(writer, "response_types_supported", []);
            WriteStringArray(writer, "scopes_supported", [OpenIdScope]);
            WriteStringArray(writer, "claim_types_supported", ["normal"]);
            WriteStringArray(
                writer,
                "claims_supported",
                ["iss", "sub", "aud", "iat", "nbf", "exp", "jti", "client_id", "token_use", "scope"]);
            writer.WriteBoolean("claims_parameter_supported", false);
            writer.WriteBoolean("request_parameter_supported", false);
            writer.WriteBoolean("request_uri_parameter_supported", false);
            writer.WriteBoolean("require_request_uri_registration", false);
            writer.WriteBoolean("tls_client_certificate_bound_access_tokens", false);
            writer.WriteEndObject();
        }).ConfigureAwait(false);
    }

    private async Task HandleJwksAsync(IHttpContext context)
    {
        if (!RequireRead(context))
        {
            return;
        }

        context.Response.Headers[HttpHeaderKey.CacheControl] = "public, max-age=300";
        await WriteJsonAsync(context, writer => SigningKey.WriteJwks(writer)).ConfigureAwait(false);
    }

    private async Task HandleTokenAsync(IHttpContext context)
    {
        if (context.Request.Method != HttpMethod.Post)
        {
            SetMethodNotAllowed(context, "POST");
            return;
        }

        IHttpFormCollection form;
        try
        {
            form = await context.ReadFormAsync(context.RequestCancelled).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            await WriteOAuthErrorAsync(context, "invalid_request").ConfigureAwait(false);
            return;
        }

        string? grantType = FormValue(form, "grant_type");
        if (string.Equals(grantType, "client_credentials", StringComparison.Ordinal))
        {
            await HandleClientCredentialsAsync(context, form).ConfigureAwait(false);
        }
        else if (string.Equals(grantType, DeviceGrant, StringComparison.Ordinal))
        {
            await HandleDeviceTokenAsync(context, form).ConfigureAwait(false);
        }
        else
        {
            await WriteOAuthErrorAsync(context, "unsupported_grant_type").ConfigureAwait(false);
        }
    }

    private async Task HandleClientCredentialsAsync(IHttpContext context, IHttpFormCollection form)
    {
        ClientAuthenticationStatus authentication = AuthenticateClient(
            context,
            form,
            allowPublicClient: false,
            out IdentityHubClientRegistration? client);
        if (authentication is ClientAuthenticationStatus.MultipleMethods)
        {
            await WriteOAuthErrorAsync(context, "invalid_request").ConfigureAwait(false);
            return;
        }

        if (authentication is not ClientAuthenticationStatus.Authenticated)
        {
            context.Response.Headers[HttpHeaderKey.WWWAuthenticate] = "Basic";
            await WriteOAuthErrorAsync(context, "invalid_client", HttpStatusCode.Unauthorized)
                .ConfigureAwait(false);
            return;
        }

        string? audience = ResolveAudience(client!, form);
        if (audience is null)
        {
            await WriteOAuthErrorAsync(context, "invalid_target").ConfigureAwait(false);
            return;
        }

        if (!TryResolveScope(form, out string? scope))
        {
            await WriteOAuthErrorAsync(context, "invalid_scope").ConfigureAwait(false);
            return;
        }

        string token = SigningKey.Issue(
            _issuer,
            client!.ClientId,
            audience,
            client.ClientId,
            scope,
            client.AccessTokenLifetime,
            DateTimeOffset.UtcNow);
        await WriteTokenResponseAsync(context, token, null, scope, client.AccessTokenLifetime)
            .ConfigureAwait(false);
    }

    private async Task HandleDeviceAuthorizationAsync(IHttpContext context)
    {
        if (!_allowDevelopmentDeviceApproval)
        {
            context.Response.StatusCode = HttpStatusCode.NotFound;
            return;
        }

        if (context.Request.Method != HttpMethod.Post)
        {
            SetMethodNotAllowed(context, "POST");
            return;
        }

        IHttpFormCollection form;
        try
        {
            form = await context.ReadFormAsync(context.RequestCancelled).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            await WriteOAuthErrorAsync(context, "invalid_request").ConfigureAwait(false);
            return;
        }

        ClientAuthenticationStatus authentication = AuthenticateClient(
            context,
            form,
            allowPublicClient: true,
            out IdentityHubClientRegistration? client);
        if (authentication is ClientAuthenticationStatus.MultipleMethods)
        {
            await WriteOAuthErrorAsync(context, "invalid_request").ConfigureAwait(false);
            return;
        }

        if (authentication is not ClientAuthenticationStatus.Authenticated)
        {
            context.Response.Headers[HttpHeaderKey.WWWAuthenticate] = "Basic";
            await WriteOAuthErrorAsync(context, "invalid_client", HttpStatusCode.Unauthorized)
                .ConfigureAwait(false);
            return;
        }

        if (!client!.AllowDeviceAuthorization)
        {
            await WriteOAuthErrorAsync(context, "unauthorized_client").ConfigureAwait(false);
            return;
        }

        string? audience = ResolveAudience(client, form);
        if (audience is null)
        {
            await WriteOAuthErrorAsync(context, "invalid_target").ConfigureAwait(false);
            return;
        }

        if (!TryResolveScope(form, out string? scope))
        {
            await WriteOAuthErrorAsync(context, "invalid_scope").ConfigureAwait(false);
            return;
        }

        DeviceAuthorization authorization = _devices.Issue(
            client,
            audience,
            scope,
            DateTimeOffset.UtcNow);
        string verificationUri = Absolute("/oauth2/device");
        context.Response.Headers[HttpHeaderKey.CacheControl] = "no-store";
        context.Response.Headers[HttpHeaderKey.Pragma] = "no-cache";
        await WriteJsonAsync(context, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("device_code", authorization.DeviceCode);
            writer.WriteString("user_code", authorization.UserCode);
            writer.WriteString("verification_uri", verificationUri);
            writer.WriteString(
                "verification_uri_complete",
                verificationUri + "?user_code=" + Uri.EscapeDataString(authorization.UserCode));
            writer.WriteNumber("expires_in", 600);
            writer.WriteNumber("interval", 1);
            writer.WriteEndObject();
        }).ConfigureAwait(false);
    }

    private async Task HandleDeviceVerificationAsync(IHttpContext context)
    {
        if (!_allowDevelopmentDeviceApproval)
        {
            context.Response.StatusCode = HttpStatusCode.NotFound;
            return;
        }

        SetDevelopmentApprovalHeaders(context);
        if (context.Request.Method == HttpMethod.Get || context.Request.Method == HttpMethod.Head)
        {
            const string html = "<!doctype html><html><body><p>This local development approval signs in as <code>development-user</code>.</p><form method=\"post\"><label>Code <input name=\"user_code\" autocomplete=\"one-time-code\" required></label><button>Approve</button></form></body></html>";
            await WriteBytesAsync(context, Encoding.UTF8.GetBytes(html), "text/html; charset=utf-8")
                .ConfigureAwait(false);
            return;
        }

        if (context.Request.Method != HttpMethod.Post)
        {
            SetMethodNotAllowed(context, "GET, HEAD, POST");
            return;
        }

        if (!HasSameOrigin(context))
        {
            context.Response.StatusCode = HttpStatusCode.Forbidden;
            return;
        }

        IHttpFormCollection form;
        try
        {
            form = await context.ReadFormAsync(context.RequestCancelled).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            await WriteOAuthErrorAsync(context, "invalid_request").ConfigureAwait(false);
            return;
        }

        string? userCode = FormValue(form, "user_code");
        if (userCode is null ||
            !_devices.Approve(userCode, DevelopmentDeviceSubject, DateTimeOffset.UtcNow))
        {
            await WriteOAuthErrorAsync(context, "invalid_request").ConfigureAwait(false);
            return;
        }

        await WriteBytesAsync(
            context,
            "Device authorization approved."u8.ToArray(),
            "text/plain; charset=utf-8").ConfigureAwait(false);
    }

    private async Task HandleDeviceTokenAsync(IHttpContext context, IHttpFormCollection form)
    {
        if (!_allowDevelopmentDeviceApproval)
        {
            await WriteOAuthErrorAsync(context, "unsupported_grant_type").ConfigureAwait(false);
            return;
        }

        string? deviceCode = FormValue(form, "device_code");
        if (deviceCode is null)
        {
            await WriteOAuthErrorAsync(context, "invalid_request").ConfigureAwait(false);
            return;
        }

        ClientAuthenticationStatus authentication = AuthenticateClient(
            context,
            form,
            allowPublicClient: true,
            out IdentityHubClientRegistration? client);
        if (authentication is ClientAuthenticationStatus.MultipleMethods)
        {
            await WriteOAuthErrorAsync(context, "invalid_request").ConfigureAwait(false);
            return;
        }

        if (authentication is not ClientAuthenticationStatus.Authenticated)
        {
            context.Response.Headers[HttpHeaderKey.WWWAuthenticate] = "Basic";
            await WriteOAuthErrorAsync(context, "invalid_client", HttpStatusCode.Unauthorized)
                .ConfigureAwait(false);
            return;
        }

        DeviceAuthorizationResult result = _devices.Poll(
            deviceCode,
            client!.ClientId,
            DateTimeOffset.UtcNow);
        if (result.Status is DeviceAuthorizationStatus.Pending)
        {
            await WriteOAuthErrorAsync(context, "authorization_pending").ConfigureAwait(false);
            return;
        }

        if (result.Status is DeviceAuthorizationStatus.Expired)
        {
            await WriteOAuthErrorAsync(context, "expired_token").ConfigureAwait(false);
            return;
        }

        if (result.Authorization is not { } authorization)
        {
            await WriteOAuthErrorAsync(context, "invalid_grant").ConfigureAwait(false);
            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        string accessToken = SigningKey.Issue(
            _issuer,
            authorization.Subject!,
            authorization.Audience,
            authorization.Client.ClientId,
            authorization.Scope,
            authorization.Client.AccessTokenLifetime,
            now);
        string? identityToken = HasScope(authorization.Scope, OpenIdScope)
            ? SigningKey.Issue(
                _issuer,
                authorization.Subject!,
                authorization.Client.ClientId,
                authorization.Client.ClientId,
                authorization.Scope,
                authorization.Client.AccessTokenLifetime,
                now,
                identityToken: true)
            : null;
        await WriteTokenResponseAsync(
            context,
            accessToken,
            identityToken,
            authorization.Scope,
            authorization.Client.AccessTokenLifetime).ConfigureAwait(false);
    }

    private async Task HandleHealthAsync(IHttpContext context, HealthReportKind kind)
    {
        if (!RequireRead(context))
        {
            return;
        }

        ResourceHealthReport report = _controlPlane is null
            ? new ResourceHealthReport(HealthStatus.Healthy, new Dictionary<string, HealthContribution>())
            : kind switch
            {
                HealthReportKind.Health => await _controlPlane.CheckHealthAsync(context.RequestCancelled).ConfigureAwait(false),
                HealthReportKind.Readiness => await _controlPlane.CheckReadinessAsync(context.RequestCancelled).ConfigureAwait(false),
                _ => await _controlPlane.CheckLivenessAsync(context.RequestCancelled).ConfigureAwait(false),
            };
        bool ready = kind is not HealthReportKind.Readiness || _applicationContext.State is HostState.Started;
        context.Response.StatusCode = report.Status is HealthStatus.Unhealthy || !ready
            ? HttpStatusCode.ServiceUnavailable
            : HttpStatusCode.Ok;
        context.Response.Headers[HttpHeaderKey.CacheControl] = "no-store, no-cache";
        await WriteJsonAsync(context, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("status", ready ? report.Status.ToString() : HealthStatus.Unhealthy.ToString());
            writer.WriteEndObject();
        }, "application/health+json; charset=utf-8").ConfigureAwait(false);
    }

    private async Task HandleEndpointsAsync(IHttpContext context)
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
            writer.WriteString("https", _endpoint.ToString());
            writer.WriteEndObject();
            writer.WriteEndObject();
        }).ConfigureAwait(false);
    }

    private async Task HandleCommandsAsync(IHttpContext context)
    {
        if (context.Request.Method == HttpMethod.Get || context.Request.Method == HttpMethod.Head)
        {
            await WriteJsonAsync(context, writer =>
            {
                writer.WriteStartObject();
                writer.WriteStartArray("acceptedCommandKinds");
                foreach (string kind in _commands.AcceptedCommandKinds) { writer.WriteStringValue(kind); }
                writer.WriteEndArray();
                writer.WriteStartArray("commands");
                foreach (ResourceCommand command in _commands.Commands)
                {
                    writer.WriteStartObject();
                    writer.WriteString("id", command.Id);
                    writer.WriteString("kind", command.Kind);
                    writer.WriteString("owner", command.Owner);
                    writer.WriteString("key", command.Key);
                    writer.WriteString("status", "Applied");
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }).ConfigureAwait(false);
            return;
        }
        if (context.Request.Method != HttpMethod.Post && context.Request.Method != HttpMethod.Delete)
        {
            context.Response.StatusCode = HttpStatusCode.MethodNotAllowed;
            context.Response.Headers[HttpHeaderKey.Allow] = "GET, HEAD, POST, DELETE";
            return;
        }
        try
        {
            using JsonDocument document = await JsonDocument.ParseAsync(context.Request.Body,
                cancellationToken: context.RequestCancelled).ConfigureAwait(false);
            JsonElement root = document.RootElement;
            var command = new ResourceCommand(
                RequiredCommandString(root, "id"), RequiredCommandString(root, "kind"),
                RequiredCommandString(root, "owner"), RequiredCommandString(root, "key"),
                root.TryGetProperty("payload", out JsonElement payload) && payload.ValueKind == JsonValueKind.String
                    ? payload.GetBytesFromBase64() : throw new JsonException("The command payload must be a base64 string."));
            if (_requireAuthentication && command.Owner != _applicationIssuer)
            {
                context.Response.StatusCode = HttpStatusCode.Forbidden;
                await WriteCommandRefusalAsync(context,
                    $"Command owner '{command.Owner}' must match authenticated issuer '{_applicationIssuer}'.").ConfigureAwait(false);
                return;
            }
            ReadOnlyMemory<byte> response = context.Request.Method == HttpMethod.Delete
                ? await _commands.DeleteCommandAsync(command, context.RequestCancelled).ConfigureAwait(false)
                : await _commands.ExecuteCommandAsync(command, context.RequestCancelled).ConfigureAwait(false);
            context.Response.StatusCode = HttpStatusCode.Ok;
            context.Response.Headers[HttpHeaderKey.ContentType] = "application/octet-stream";
            await context.Response.Body.WriteAsync(response, context.RequestCancelled).ConfigureAwait(false);
        }
        catch (ResourceCommandRejectedException exception)
        {
            context.Response.StatusCode = HttpStatusCode.Conflict;
            await WriteCommandRefusalAsync(context, exception.Detail).ConfigureAwait(false);
        }
        catch (NotSupportedException exception)
        {
            context.Response.StatusCode = HttpStatusCode.NotImplemented;
            await WriteCommandRefusalAsync(context, exception.Message).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JsonException or FormatException or ArgumentException)
        {
            context.Response.StatusCode = HttpStatusCode.BadRequest;
            await WriteCommandRefusalAsync(context, exception.Message).ConfigureAwait(false);
        }
    }

    private static Task WriteCommandRefusalAsync(IHttpContext context, string detail) =>
        WriteJsonAsync(context, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("status", "Rejected");
            writer.WriteString("detail", detail);
            writer.WriteEndObject();
        });

    private static string RequiredCommandString(JsonElement payload, string property)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty(property, out JsonElement value) ||
            value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new JsonException($"An identity command requires a nonblank '{property}'.");
        }
        return value.GetString()!;
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

    private BootstrapTokenStatus Authorize(IHttpContext context)
    {
        if (!_requireAuthentication)
        {
            return BootstrapTokenStatus.Authorized;
        }

        if (!context.Request.Headers.TryGetValue(HttpHeaderKey.Authorization, out HttpHeaderValue authorization) ||
            !authorization.Value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return BootstrapTokenStatus.Unauthorized;
        }

        return _bootstrapVerifier!.Validate(
            authorization.Value["Bearer ".Length..],
            _resourceAudience,
            DateTimeOffset.UtcNow);
    }

    private ClientAuthenticationStatus AuthenticateClient(
        IHttpContext context,
        IHttpFormCollection form,
        bool allowPublicClient,
        out IdentityHubClientRegistration? client)
    {
        client = null;
        bool hasBodyClientId = form.TryGetValue("client_id", out _);
        bool hasBodySecret = form.TryGetValue("client_secret", out _);
        bool hasAuthorization = context.Request.Headers.TryGetValue(
            HttpHeaderKey.Authorization,
            out HttpHeaderValue header);
        if (hasAuthorization && hasBodySecret)
        {
            return ClientAuthenticationStatus.MultipleMethods;
        }

        string? clientId;
        string? secret;
        if (hasAuthorization)
        {
            if (!TryParseBasicCredentials(header, out clientId, out secret))
            {
                return ClientAuthenticationStatus.Invalid;
            }

            string? bodyClientId = FormValue(form, "client_id");
            if ((hasBodyClientId && bodyClientId is null) ||
                (bodyClientId is not null &&
                 !string.Equals(bodyClientId, clientId, StringComparison.Ordinal)))
            {
                return ClientAuthenticationStatus.Invalid;
            }
        }
        else
        {
            clientId = FormValue(form, "client_id");
            secret = FormValue(form, "client_secret");
            if ((hasBodyClientId && clientId is null) || (hasBodySecret && secret is null))
            {
                return ClientAuthenticationStatus.Invalid;
            }
        }

        if (clientId is null || !_registry.TryGetClient(clientId, out client))
        {
            client = null;
            return ClientAuthenticationStatus.Invalid;
        }

        if (secret is not null)
        {
            return client.AllowsClientCredentials && client.VerifySecret(secret)
                ? ClientAuthenticationStatus.Authenticated
                : ClientAuthenticationStatus.Invalid;
        }

        return allowPublicClient && !client.AllowsClientCredentials
            ? ClientAuthenticationStatus.Authenticated
            : ClientAuthenticationStatus.Invalid;
    }

    private static bool TryParseBasicCredentials(
        HttpHeaderValue header,
        out string? clientId,
        out string? secret)
    {
        clientId = null;
        secret = null;
        if (!header.Value.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            string decoded = Encoding.UTF8.GetString(
                Convert.FromBase64String(header.Value["Basic ".Length..]));
            int separator = decoded.IndexOf(':');
            if (separator < 0)
            {
                return false;
            }

            clientId = Uri.UnescapeDataString(decoded[..separator]);
            secret = Uri.UnescapeDataString(decoded[(separator + 1)..]);
            return !string.IsNullOrWhiteSpace(clientId) && !string.IsNullOrWhiteSpace(secret);
        }
        catch (Exception exception) when (exception is FormatException or UriFormatException)
        {
            clientId = null;
            secret = null;
            return false;
        }
    }

    private static string? ResolveAudience(
        IdentityHubClientRegistration client,
        IHttpFormCollection form)
    {
        string? audience = FormValue(form, "audience") ?? FormValue(form, "resource");
        if (audience is null && client.Audiences.Count is 1)
        {
            audience = client.Audiences[0];
        }

        return audience is not null && client.AllowsAudience(audience) ? audience : null;
    }

    private static string? FormValue(IHttpFormCollection form, string name)
        => form.TryGetValue(name, out HttpQueryValue value) && !string.IsNullOrWhiteSpace(value.Value)
            ? value.Value
            : null;

    private static bool TryResolveScope(IHttpFormCollection form, out string? scope)
    {
        scope = null;
        if (!form.TryGetValue("scope", out HttpQueryValue value) ||
            string.IsNullOrWhiteSpace(value.Value))
        {
            return true;
        }

        string[] requested = value.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int index = 0; index < requested.Length; index++)
        {
            if (!string.Equals(requested[index], OpenIdScope, StringComparison.Ordinal))
            {
                return false;
            }
        }

        scope = OpenIdScope;
        return true;
    }

    private static bool HasScope(string? scopes, string expected)
    {
        if (scopes is null)
        {
            return false;
        }

        foreach (string scope in scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(scope, expected, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private bool HasSameOrigin(IHttpContext context)
    {
        if (!context.Request.Headers.TryGetValue(HttpHeaderKey.Origin, out HttpHeaderValue origin))
        {
            return true;
        }

        return Uri.TryCreate(origin.Value, UriKind.Absolute, out Uri? supplied) &&
            string.Equals(supplied.Scheme, _endpoint.Scheme, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(supplied.IdnHost, _endpoint.IdnHost, StringComparison.OrdinalIgnoreCase) &&
            supplied.Port == _endpoint.Port &&
            string.IsNullOrEmpty(supplied.UserInfo) &&
            string.IsNullOrEmpty(supplied.Query) &&
            string.IsNullOrEmpty(supplied.Fragment);
    }

    private static void SetDevelopmentApprovalHeaders(IHttpContext context)
    {
        context.Response.Headers[HttpHeaderKey.CacheControl] = "no-store";
        context.Response.Headers[HttpHeaderKey.Pragma] = "no-cache";
        context.Response.Headers[HttpHeaderKey.ContentSecurityPolicy] =
            "default-src 'none'; form-action 'self'; base-uri 'none'; frame-ancestors 'none'";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers["X-Frame-Options"] = "DENY";
        context.Response.Headers[HttpHeaderKey.XContentTypeOptions] = "nosniff";
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

    private static void SetAuthorizationFailure(IHttpContext context, BootstrapTokenStatus status)
    {
        context.Response.StatusCode = status is BootstrapTokenStatus.Forbidden
            ? HttpStatusCode.Forbidden
            : HttpStatusCode.Unauthorized;
        if (status is BootstrapTokenStatus.Unauthorized)
        {
            context.Response.Headers[HttpHeaderKey.WWWAuthenticate] = "Bearer";
        }
    }

    private static void SetMethodNotAllowed(IHttpContext context, string allow)
    {
        context.Response.StatusCode = HttpStatusCode.MethodNotAllowed;
        context.Response.Headers[HttpHeaderKey.Allow] = allow;
    }

    private async Task WriteTokenResponseAsync(
        IHttpContext context,
        string accessToken,
        string? identityToken,
        string? scope,
        TimeSpan lifetime)
    {
        context.Response.Headers[HttpHeaderKey.CacheControl] = "no-store";
        context.Response.Headers[HttpHeaderKey.Pragma] = "no-cache";
        await WriteJsonAsync(context, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("access_token", accessToken);
            writer.WriteString("token_type", "Bearer");
            writer.WriteNumber("expires_in", (long)lifetime.TotalSeconds);
            if (scope is not null)
            {
                writer.WriteString("scope", scope);
            }

            if (identityToken is not null)
            {
                writer.WriteString("id_token", identityToken);
            }

            writer.WriteEndObject();
        }).ConfigureAwait(false);
    }

    private static Task WriteOAuthErrorAsync(IHttpContext context, string error)
        => WriteOAuthErrorAsync(context, error, HttpStatusCode.BadRequest);

    private static async Task WriteOAuthErrorAsync(
        IHttpContext context,
        string error,
        HttpStatusCode status)
    {
        context.Response.StatusCode = status;
        context.Response.Headers[HttpHeaderKey.CacheControl] = "no-store";
        context.Response.Headers[HttpHeaderKey.Pragma] = "no-cache";
        await WriteJsonAsync(context, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("error", error);
            writer.WriteEndObject();
        }).ConfigureAwait(false);
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

    private static void WriteStringArray(Utf8JsonWriter writer, string name, string[] values)
    {
        writer.WritePropertyName(name);
        writer.WriteStartArray();
        for (int index = 0; index < values.Length; index++)
        {
            writer.WriteStringValue(values[index]);
        }

        writer.WriteEndArray();
    }

    private string Route(string path) => _basePath + path;

    private string Absolute(string path) => _issuer + path;

    private IdentitySigningKey SigningKey => _signingKey ?? throw new InvalidOperationException(
        "The IdentityHub signing key is not initialized.");

    private static IPAddress ResolveBindAddress(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return IPAddress.Loopback;
        }

        return IPAddress.TryParse(host, out IPAddress? address)
            ? address
            : throw new InvalidOperationException(
                $"The IdentityHub endpoint host '{host}' is not a bindable IP address.");
    }

    private SslStreamCertificateContext CreateDevelopmentServerCertificateContext(string host)
    {
        _serverCertificate = _resourceContext.CreateDevelopmentEndpointCertificate(host);
        return SslStreamCertificateContext.Create(_serverCertificate, null, offline: true);
    }

    private enum ClientAuthenticationStatus
    {
        Invalid,
        MultipleMethods,
        Authenticated,
    }

    private enum HealthReportKind
    {
        Health,
        Readiness,
        Liveness,
    }
}
