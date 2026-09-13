using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.SecretStore.Client;

internal sealed class HttpSecretStoreClient : ISecretStoreClient
{
    private const string certificateRoute = "/cohesion/v1/certificates";
    private const string commandRoute = "/cohesion/v1/commands";
    private const string secretRoute = "/cohesion/v1/secrets";

    private readonly ClientCredential _credential;
    private readonly Uri _endpoint;
    private readonly HttpMessageInvoker _transport;
    private readonly bool _controlPlaneAddress;

    internal HttpSecretStoreClient(
        Uri endpoint,
        ClientCredential credential,
        HttpMessageInvoker transport,
        bool controlPlaneAddress = false)
    {
        _endpoint = endpoint;
        _credential = credential;
        _transport = transport;
        _controlPlaneAddress = controlPlaneAddress;
    }

    public async Task<ReadOnlyMemory<byte>> GetSecretAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using HttpRequestMessage request = CreateRequest(
            HttpMethod.Get,
            secretRoute,
            "application/octet-stream",
            "path",
            path);
        using HttpResponseMessage response = await _transport
            .SendAsync(request, cancellationToken)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        byte[] value = await response.Content
            .ReadAsByteArrayAsync(cancellationToken)
            .ConfigureAwait(false);

        return value;
    }

    public async Task<string> GetCertificateAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        using HttpRequestMessage request = CreateRequest(
            HttpMethod.Get,
            certificateRoute,
            "application/x-pem-file",
            "name",
            name);
        using HttpResponseMessage response = await _transport
            .SendAsync(request, cancellationToken)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        string pem = await response.Content
            .ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrEmpty(pem))
        {
            throw new InvalidDataException("The secret-store endpoint returned an empty certificate.");
        }

        return pem;
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
                SecretStoreClientJsonContext.Default.ResourceCommand));
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
        using HttpRequestMessage request = CreateRequest(method, commandRoute, "application/octet-stream");
        request.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(
            command, SecretStoreClientJsonContext.Default.ResourceCommand));
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using HttpResponseMessage response = await _transport.SendAsync(request, cancellationToken).ConfigureAwait(false);
        byte[] content = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        string status = response.IsSuccessStatusCode ? method == HttpMethod.Delete ? "Deleted" : "Applied" : "Rejected";
        string? detail = response.IsSuccessStatusCode ? null :
            $"SecretStore command '{command.Kind}' was refused: HTTP {(int)response.StatusCode} {response.ReasonPhrase}.";
        if (content.Length > 0 && response.Content.Headers.ContentType?.MediaType == "application/json")
        {
            using JsonDocument document = JsonDocument.Parse(content);
            JsonElement root = document.RootElement;
            if (root.TryGetProperty("status", out JsonElement observed) && observed.ValueKind == JsonValueKind.String)
            {
                status = observed.GetString()!;
            }
            if (root.TryGetProperty("detail", out JsonElement reason) && reason.ValueKind == JsonValueKind.String)
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
            Path = $"{_endpoint.AbsolutePath.TrimEnd('/')}{(_controlPlaneAddress ? route["/cohesion/v1".Length..] : route)}"
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
