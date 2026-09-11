using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Health;
using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Http.Connections;
using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.Hosting;

using HttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;

namespace Assimalign.Cohesion.ConfigurationStore.Hosting;

internal sealed class ConfigurationEndpointService : IHostService, IDisposable
{
    internal const string RemoveValueCommand = "configurationstore.remove-value";
    internal const string SetValueCommand = "configurationstore.set-value";

    private readonly ConfigurationStoreApplicationContext _applicationContext;
    private readonly string _audience;
    private readonly string _basePath;
    private readonly IResourceControlPlane? _controlPlane;
    private readonly IResourceControlPlane _commands;
    private readonly Uri _endpoint;
    private readonly WebApplication _host;
    private readonly bool _requireAuthentication;
    private readonly ConfigurationStoreRepository _repository;
    private readonly ResourceContext? _resourceContext;
    private BootstrapTokenVerifier? _tokenVerifier;

    internal ConfigurationEndpointService(
        Uri endpoint,
        ConfigurationStoreRepository repository,
        IResourceControlPlane? controlPlane,
        ResourceContext? resourceContext,
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
        _controlPlane = controlPlane;
        _commands = controlPlane ?? ResourceControlPlane.Create(new[] { SetValueCommand, RemoveValueCommand });
        foreach (string kind in _commands.AcceptedCommandKinds)
        {
            if (kind is SetValueCommand or RemoveValueCommand)
            {
                _commands.RegisterCommandHandler(new ConfigurationResourceCommandHandler(kind, repository));
            }
        }
        _resourceContext = resourceContext;
        _applicationContext = applicationContext;
        _requireAuthentication = resourceContext?.GatewayName is not null;
        _audience = resourceContext?.ResourceName ?? string.Empty;
        if (_requireAuthentication && string.IsNullOrWhiteSpace(_audience))
        {
            throw new InvalidOperationException(
                "A gateway-managed ConfigurationStore requires an ambient resource name.");
        }

        _basePath = endpoint.AbsolutePath == "/"
            ? string.Empty
            : endpoint.AbsolutePath.TrimEnd('/');

        IPAddress address = ResolveBindAddress(endpoint.IdnHost);
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Server.UseServer(options =>
            options.UseHttp1(tcp => tcp.EndPoint = new IPEndPoint(address, endpoint.Port)));

        _host = builder.Build();
        IWebApplicationPipelineBuilder pipeline = _host;
        pipeline.Use(next => context => InvokeAsync(context, next));
    }

    public ServiceId Id { get; } = ServiceId.New();

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _repository.InitializeAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ConfigurationTrustedIssuer> trustedIssuers =
            await ConfigurationTrustedIssuerStore.LoadAsync(
                    _repository.DataPath,
                    _resourceContext,
                    _requireAuthentication,
                    cancellationToken)
                .ConfigureAwait(false);
        _tokenVerifier = new BootstrapTokenVerifier(trustedIssuers);
        _controlPlane?.ObserveEndpoint("api", _endpoint);
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

    private async Task InvokeAsync(IHttpContext context, WebApplicationMiddleware next)
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

        BootstrapTokenValidation authorization = new(
            BootstrapTokenValidationStatus.Authorized,
            null);
        if (isNamespaced)
        {
            authorization = Authorize(context);
            if (authorization.Status is not BootstrapTokenValidationStatus.Authorized)
            {
                SetAuthorizationFailure(context, authorization.Status);
                if (path == Route("/cohesion/v1/commands"))
                {
                    await WriteCommandStatusAsync(context, "Rejected", "The configuration command credential is missing, invalid, or not authorized for this resource.").ConfigureAwait(false);
                }
                return;
            }
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
            return;
        }

        if (path == Route("/cohesion/v1/stop"))
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
            return;
        }

        if (path == Route("/cohesion/v1/namespaces"))
        {
            await HandleNamespacesAsync(context).ConfigureAwait(false);
            return;
        }

        if (path == Route("/cohesion/v1/commands"))
        {
            await HandleCommandsAsync(context, authorization.Issuer).ConfigureAwait(false);
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

    private async Task HandleCommandsAsync(IHttpContext context, string? authenticatedIssuer)
    {
        if (IsRead(context))
        {
            await WriteJsonAsync(context, writer =>
            {
                writer.WriteStartObject();
                writer.WritePropertyName("acceptedCommandKinds");
                writer.WriteStartArray();
                writer.WriteStringValue(SetValueCommand);
                writer.WriteStringValue(RemoveValueCommand);
                writer.WriteEndArray();
                writer.WritePropertyName("commands");
                writer.WriteStartArray();
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
            SetMethodNotAllowed(context, "GET, HEAD, POST, DELETE");
            return;
        }

        ResourceCommand command;
        try
        {
            command = await ReadCommandAsync(context).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JsonException or FormatException)
        {
            context.Response.StatusCode = HttpStatusCode.BadRequest;
            await WriteCommandStatusAsync(context, "Rejected", exception.Message).ConfigureAwait(false);
            return;
        }

        if (_requireAuthentication &&
            !string.Equals(command.Owner, authenticatedIssuer, StringComparison.Ordinal))
        {
            context.Response.StatusCode = HttpStatusCode.Forbidden;
            await WriteCommandStatusAsync(context, "Rejected", $"Command owner '{command.Owner}' must match authenticated issuer '{authenticatedIssuer}'.").ConfigureAwait(false);
            return;
        }

        try
        {
            if (context.Request.Method == HttpMethod.Delete)
            {
                await _commands.DeleteCommandAsync(command, context.RequestCancelled).ConfigureAwait(false);
            }
            else
            {
                await _commands.ExecuteCommandAsync(command, context.RequestCancelled).ConfigureAwait(false);
            }
            context.Response.StatusCode = HttpStatusCode.Ok;
            await WriteCommandStatusAsync(context, context.Request.Method == HttpMethod.Delete ? "Deleted" : "Applied", null).ConfigureAwait(false);
        }
        catch (ConfigurationNamespaceNotFoundException exception)
        {
            context.Response.StatusCode = HttpStatusCode.NotFound;
            await WriteCommandStatusAsync(context, "Rejected", exception.Detail).ConfigureAwait(false);
        }
        catch (ResourceCommandRejectedException exception)
        {
            context.Response.StatusCode = HttpStatusCode.Conflict;
            await WriteCommandStatusAsync(context, "Rejected", exception.Detail).ConfigureAwait(false);
        }
        catch (NotSupportedException exception)
        {
            context.Response.StatusCode = HttpStatusCode.NotImplemented;
            await WriteCommandStatusAsync(context, "Rejected", exception.Message).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            context.Response.StatusCode = HttpStatusCode.BadRequest;
            await WriteCommandStatusAsync(context, "Rejected", exception.Message).ConfigureAwait(false);
        }
    }

    private static Task WriteCommandStatusAsync(IHttpContext context, string status, string? detail) =>
        WriteJsonAsync(context, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("status", status);
            writer.WriteString("detail", detail);
            writer.WriteEndObject();
        });

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
            _applicationContext.State is HostState.Started;
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

                if (contribution.Data is { Count: > 0 } data)
                {
                    writer.WritePropertyName("data");
                    writer.WriteStartObject();
                    foreach (KeyValuePair<string, object> item in data)
                    {
                        writer.WritePropertyName(item.Key);
                        WriteDataValue(writer, item.Value);
                    }

                    writer.WriteEndObject();
                }

                writer.WriteEndObject();
            }

            if (!ready)
            {
                writer.WritePropertyName("cohesion.host");
                writer.WriteStartObject();
                writer.WriteString("status", HealthStatus.Unhealthy.ToString());
                writer.WriteString("description", "The ConfigurationStore host has not completed startup.");
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }, "application/health+json; charset=utf-8").ConfigureAwait(false);
    }

    private static void WriteDataValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case string text:
                writer.WriteStringValue(text);
                break;
            case bool flag:
                writer.WriteBooleanValue(flag);
                break;
            case int number:
                writer.WriteNumberValue(number);
                break;
            case long number:
                writer.WriteNumberValue(number);
                break;
            case double number:
                writer.WriteNumberValue(number);
                break;
            case float number:
                writer.WriteNumberValue(number);
                break;
            case decimal number:
                writer.WriteNumberValue(number);
                break;
            default:
                writer.WriteStringValue(value.ToString());
                break;
        }
    }

    private BootstrapTokenValidation Authorize(IHttpContext context)
    {
        if (!_requireAuthentication)
        {
            return new BootstrapTokenValidation(BootstrapTokenValidationStatus.Authorized, null);
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
    {
        return context.Request.Method == HttpMethod.Get || context.Request.Method == HttpMethod.Head;
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

    private static async Task<ResourceCommand> ReadCommandAsync(IHttpContext context)
    {
        using JsonDocument document = await JsonDocument.ParseAsync(
                context.Request.Body,
                cancellationToken: context.RequestCancelled)
            .ConfigureAwait(false);
        JsonElement root = document.RootElement;
        if (root.ValueKind is not JsonValueKind.Object)
        {
            throw new JsonException("A configuration command must be a JSON object.");
        }

        if (root.TryGetProperty("payload", out JsonElement rawPayload) && rawPayload.ValueKind is not JsonValueKind.String)
        {
            throw new JsonException("The configuration command payload must be a base64 string.");
        }

        byte[] payload = root.TryGetProperty("payload", out JsonElement payloadProperty)
            ? payloadProperty.GetBytesFromBase64()
            : Array.Empty<byte>();
        return new ResourceCommand(
            GetRequiredString(root, "id"),
            GetRequiredString(root, "kind"),
            GetRequiredString(root, "owner"),
            GetRequiredString(root, "key"),
            payload);
    }

    private static string GetRequiredString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement property) ||
            property.ValueKind is not JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new JsonException($"Configuration command property '{name}' is required.");
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
}
