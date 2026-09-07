using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Core;

namespace Assimalign.Cohesion.SecretStore.Client;

internal sealed class HttpSecretStoreClient : ISecretStoreClient
{
    private const string certificateRoute = "/cohesion/v1/certificates";
    private const string commandRoute = "/cohesion/v1/commands";
    private const string secretRoute = "/cohesion/v1/secrets";

    private readonly ClientCredential _credential;
    private readonly EndpointAddress _endpoint;
    private readonly HttpMessageInvoker _transport;

    internal HttpSecretStoreClient(
        EndpointAddress endpoint,
        ClientCredential credential,
        HttpMessageInvoker transport)
    {
        _endpoint = endpoint;
        _credential = credential;
        _transport = transport;
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

    private HttpRequestMessage CreateRequest(
        HttpMethod method,
        string route,
        string acceptMediaType,
        string? queryName = null,
        string? queryValue = null)
    {
        var uriBuilder = new UriBuilder(_endpoint.Url)
        {
            Path = $"{_endpoint.Url.AbsolutePath.TrimEnd('/')}{route}"
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
