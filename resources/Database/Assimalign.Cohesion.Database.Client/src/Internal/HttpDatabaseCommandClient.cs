using System;
using System.Buffers;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Client.Internal;

internal sealed class HttpDatabaseCommandClient : IDatabaseCommandClient
{
    private readonly Uri _address;
    private readonly string _bearerToken;
    private readonly HttpMessageInvoker _transport;
    private readonly bool _ownsTransport;

    /// <summary>
    /// Initializes a new instance of the <see cref="HttpDatabaseCommandClient"/> class.
    /// </summary>
    /// <param name="address">The base address of the database command endpoint.</param>
    /// <param name="bearerToken">The bearer token sent in each request's authorization header.</param>
    /// <param name="transport">The HTTP message invoker that sends command requests.</param>
    /// <param name="ownsTransport"><see langword="true"/> to dispose <paramref name="transport"/> when this client is disposed.</param>
    public HttpDatabaseCommandClient(Uri address, string bearerToken, HttpMessageInvoker transport, bool ownsTransport)
    {
        _address = address;
        _bearerToken = bearerToken;
        _transport = transport;
        _ownsTransport = ownsTransport;
    }

    public ValueTask<ResourceCommandObservation> SendCommandAsync(ResourceCommand command, CancellationToken cancellationToken = default) =>
        SendAsync(command, HttpMethod.Post, cancellationToken);

    public ValueTask<ResourceCommandObservation> DeleteCommandAsync(ResourceCommand command, CancellationToken cancellationToken = default) =>
        SendAsync(command, HttpMethod.Delete, cancellationToken);

    public void Dispose()
    {
        if (_ownsTransport)
        {
            _transport.Dispose();
        }
    }

    private async ValueTask<ResourceCommandObservation> SendAsync(ResourceCommand command, HttpMethod method, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("id", command.Id);
            writer.WriteString("kind", command.Kind);
            writer.WriteString("owner", command.Owner);
            writer.WriteString("key", command.Key);
            writer.WriteBase64String("payload", command.Payload.Span);
            writer.WriteEndObject();
        }
        var endpoint = new UriBuilder(_address) { Path = _address.AbsolutePath.TrimEnd('/') + "/commands" };
        using var request = new HttpRequestMessage(method, endpoint.Uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _bearerToken);
        request.Content = new ByteArrayContent(buffer.WrittenSpan.ToArray());
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using HttpResponseMessage response = await _transport.SendAsync(request, cancellationToken).ConfigureAwait(false);
        byte[] content = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        string status = response.IsSuccessStatusCode ? method == HttpMethod.Delete ? "Deleted" : "Applied" : "Rejected";
        string? detail = response.IsSuccessStatusCode ? null : $"Database command '{command.Kind}' was refused: HTTP {(int)response.StatusCode} {response.ReasonPhrase}.";
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
}
