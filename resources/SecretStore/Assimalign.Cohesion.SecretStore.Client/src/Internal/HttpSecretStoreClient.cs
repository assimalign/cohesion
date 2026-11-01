using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.SecretStore.Client.Internal;

[global::System.Diagnostics.DebuggerDisplay("HttpSecretStoreClient")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class HttpSecretStoreClient : ISecretStoreClient
{
    private const string certificateRoute = "/cohesion/v1/certificates";
    private const string secretRoute = "/cohesion/v1/secrets";

    private readonly ClientCredential _credential;
    private readonly Uri _endpoint;
    private readonly HttpMessageInvoker _transport;

    internal HttpSecretStoreClient(
        Uri endpoint,
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
        SecretStoreCertificateReference name,
        CancellationToken cancellationToken = default)
    {
        if (name.Value is null)
        {
            throw new ArgumentException(ErrorMessages.CertificateReferenceInvalid, nameof(name));
        }

        using HttpRequestMessage request = CreateRequest(
            HttpMethod.Get,
            certificateRoute,
            "application/x-pem-file",
            "name",
            name.Value);
        using HttpResponseMessage response = await _transport
            .SendAsync(request, cancellationToken)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        string pem = await response.Content
            .ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrEmpty(pem))
        {
            throw new InvalidDataException(ErrorMessages.CertificateEmpty);
        }

        return pem;
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

    private sealed class DebugView
    {
        public DebugView(HttpSecretStoreClient value)
        {
        }
    }
}
