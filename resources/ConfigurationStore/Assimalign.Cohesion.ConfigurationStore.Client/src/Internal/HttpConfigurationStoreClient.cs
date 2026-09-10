using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ConfigurationStore.Client;

internal sealed class HttpConfigurationStoreClient : IConfigurationStoreClient
{
    private const string commandRoute = "/cohesion/v1/commands";
    private const string namespaceRoute = "/cohesion/v1/namespaces";

    private readonly ClientCredential _credential;
    private readonly Uri _endpoint;
    private readonly HttpMessageInvoker _transport;

    internal HttpConfigurationStoreClient(
        Uri endpoint,
        ClientCredential credential,
        HttpMessageInvoker transport)
    {
        _endpoint = endpoint;
        _credential = credential;
        _transport = transport;
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
