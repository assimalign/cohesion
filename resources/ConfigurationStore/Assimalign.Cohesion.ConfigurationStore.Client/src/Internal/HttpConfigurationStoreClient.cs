using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ConfigurationStore.Client.Internal;

internal sealed class HttpConfigurationStoreClient : IConfigurationStoreClient
{
    private const string commandRoute = "/cohesion/v1/commands";
    private const string namespaceRoute = "/cohesion/v1/namespaces";

    private readonly ClientCredential _credential;
    private readonly Uri _endpoint;
    private readonly HttpMessageInvoker _transport;
    private readonly bool _commandControlPlanePath;

    internal HttpConfigurationStoreClient(
        Uri endpoint,
        ClientCredential credential,
        HttpMessageInvoker transport,
        bool commandControlPlanePath = false)
    {
        _endpoint = endpoint;
        _credential = credential;
        _transport = transport;
        _commandControlPlanePath = commandControlPlanePath;
    }

    public async Task<IReadOnlyList<string>> ListNamespacesAsync(
        CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = CreateRequest(
            HttpMethod.Get,
            namespaceRoute,
            "application/json");
        using HttpResponseMessage response = await _transport
            .SendAsync(request, cancellationToken)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        byte[] document = await response.Content
            .ReadAsByteArrayAsync(cancellationToken)
            .ConfigureAwait(false);

        List<string>? names = JsonSerializer.Deserialize(
            document,
            ConfigurationStoreClientJsonContext.Default.NamespaceNames);

        return names is null
            ? throw new JsonException("The configuration-store endpoint returned a null namespace list.")
            : new ReadOnlyCollection<string>(names);
    }

    public async Task<IReadOnlyDictionary<string, string?>> GetNamespaceAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        using HttpRequestMessage request = CreateRequest(
            HttpMethod.Get,
            namespaceRoute,
            "application/json",
            "name",
            name);
        using HttpResponseMessage response = await _transport
            .SendAsync(request, cancellationToken)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        byte[] document = await response.Content
            .ReadAsByteArrayAsync(cancellationToken)
            .ConfigureAwait(false);

        Dictionary<string, string?>? values = JsonSerializer.Deserialize(
            document,
            ConfigurationStoreClientJsonContext.Default.NamespaceValues);

        return values is null
            ? throw new JsonException("The configuration-store endpoint returned a null namespace document.")
            : new ReadOnlyDictionary<string, string?>(values);
    }

    public async Task SendCommandAsync(
        ResourceCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        using HttpRequestMessage request = CreateRequest(
            HttpMethod.Post,
            commandRoute,
            "application/octet-stream");
        request.Content = new ByteArrayContent(
            JsonSerializer.SerializeToUtf8Bytes(
                command,
                ConfigurationStoreClientJsonContext.Default.ResourceCommand));
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using HttpResponseMessage response = await _transport
            .SendAsync(request, cancellationToken)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
    }

    public ValueTask<ResourceCommandObservation> ObserveCommandAsync(ResourceCommand command, CancellationToken cancellationToken = default) =>
        ObserveAsync(command, HttpMethod.Post, cancellationToken);

    public ValueTask<ResourceCommandObservation> DeleteCommandAsync(ResourceCommand command, CancellationToken cancellationToken = default) =>
        ObserveAsync(command, HttpMethod.Delete, cancellationToken);

    private async ValueTask<ResourceCommandObservation> ObserveAsync(ResourceCommand command, HttpMethod method, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        using HttpRequestMessage request = CreateRequest(method, _commandControlPlanePath ? "/commands" : commandRoute, "application/json");
        request.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(command, ConfigurationStoreClientJsonContext.Default.ResourceCommand));
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using HttpResponseMessage response = await _transport.SendAsync(request, cancellationToken).ConfigureAwait(false);
        byte[] content = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        string status = response.IsSuccessStatusCode ? method == HttpMethod.Delete ? "Deleted" : "Applied" : "Rejected";
        string? detail = response.IsSuccessStatusCode ? null : $"Configuration command '{command.Kind}' was refused: HTTP {(int)response.StatusCode} {response.ReasonPhrase}.";
        if (content.Length > 0 && response.Content.Headers.ContentType?.MediaType == "application/json")
        {
            using JsonDocument document = JsonDocument.Parse(content);
            JsonElement root = document.RootElement;
            if (root.TryGetProperty("status", out JsonElement observed) && observed.ValueKind is JsonValueKind.String)
            {
                status = observed.GetString()!;
            }
            if (root.TryGetProperty("detail", out JsonElement reason) && reason.ValueKind is JsonValueKind.String)
            {
                detail = reason.GetString();
            }
        }
        return new ResourceCommandObservation(response.IsSuccessStatusCode ? status : "Rejected", detail);
    }

    private HttpRequestMessage CreateRequest(
        HttpMethod method,
        string route,
        string acceptMediaType,
        string? queryName = null,
        string? queryValue = null)
    {
        var uriBuilder = new UriBuilder(_endpoint)
        {
            Path = $"{_endpoint.AbsolutePath.TrimEnd('/')}{route}"
        };

        if (queryName is not null && queryValue is not null)
        {
            uriBuilder.Query = $"{queryName}={Uri.EscapeDataString(queryValue)}";
        }

        var request = new HttpRequestMessage(method, uriBuilder.Uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(acceptMediaType));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _credential.Token);
        return request;
    }
}
