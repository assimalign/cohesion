using System;
using System.Buffers;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Client;

/// <summary>Delivers declarative commands to the Database resource's admin control plane.</summary>
/// <remarks>
/// <see cref="Create(Uri, string)"/> and <see cref="Create(Uri, string, HttpMessageInvoker)"/> perform no
/// network I/O. The caller disposes the client. Redirect following and cookies are disabled on the transport
/// the client creates.
/// </remarks>
public sealed class DatabaseCommandClient : IDisposable
{
    private readonly Uri _address;
    private readonly string _bearerToken;
    private readonly HttpMessageInvoker _transport;
    private readonly bool _ownsTransport;

    private DatabaseCommandClient(Uri address, string bearerToken, HttpMessageInvoker transport, bool ownsTransport)
    {
        _address = address;
        _bearerToken = bearerToken;
        _transport = transport;
        _ownsTransport = ownsTransport;
    }

    /// <summary>Creates a command client without performing network I/O.</summary>
    /// <param name="controlPlaneAddress">The full HTTP control-plane base address, including its manifest path.</param>
    /// <param name="bearerToken">The opaque bootstrap credential.</param>
    /// <returns>A caller-owned command client.</returns>
    /// <exception cref="ArgumentException">The address is not HTTP(S) or the credential is blank.</exception>
    public static DatabaseCommandClient Create(Uri controlPlaneAddress, string bearerToken)
    {
        ValidateArguments(controlPlaneAddress, bearerToken);
        return new DatabaseCommandClient(controlPlaneAddress, bearerToken,
            new HttpMessageInvoker(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }),
            ownsTransport: true);
    }

    /// <summary>Creates a command client with a caller-owned transport, including its TLS trust policy.</summary>
    /// <param name="controlPlaneAddress">The full HTTP(S) control-plane address, including its manifest path.</param>
    /// <param name="bearerToken">The opaque bootstrap credential.</param>
    /// <param name="transport">The transport used to deliver commands.</param>
    /// <returns>A command client using the supplied transport.</returns>
    /// <remarks>The transport is owned by the caller: disposing the returned client does not dispose it.</remarks>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">The address is not HTTP(S) or the credential is blank.</exception>
    public static DatabaseCommandClient Create(
        Uri controlPlaneAddress, string bearerToken, HttpMessageInvoker transport)
    {
        ValidateArguments(controlPlaneAddress, bearerToken);
        ArgumentNullException.ThrowIfNull(transport);
        return new DatabaseCommandClient(controlPlaneAddress, bearerToken, transport, ownsTransport: false);
    }

    /// <summary>Applies a declaration and observes the provider's result.</summary>
    /// <param name="command">The command envelope.</param>
    /// <param name="cancellationToken">Cancels transport and response reading.</param>
    /// <returns>The observed status and provider detail.</returns>
    /// <exception cref="ArgumentNullException">The command is null.</exception>
    /// <exception cref="HttpRequestException">The endpoint cannot be reached.</exception>
    /// <exception cref="JsonException">The response declares <c>application/json</c> but its body is malformed.</exception>
    /// <exception cref="OperationCanceledException">The request or the response read is canceled.</exception>
    /// <exception cref="ObjectDisposedException">The transport is disposed: this client created it and was disposed, or the caller disposed its own.</exception>
    public ValueTask<ResourceCommandObservation> SendCommandAsync(ResourceCommand command, CancellationToken cancellationToken = default) =>
        SendAsync(command, HttpMethod.Post, cancellationToken);

    /// <summary>Deletes an owned declaration and observes the provider's result.</summary>
    /// <param name="command">The previously applied command envelope.</param>
    /// <param name="cancellationToken">Cancels transport and response reading.</param>
    /// <returns>The observed status and provider detail.</returns>
    /// <exception cref="ArgumentNullException">The command is null.</exception>
    /// <exception cref="HttpRequestException">The endpoint cannot be reached.</exception>
    /// <exception cref="JsonException">The response declares <c>application/json</c> but its body is malformed.</exception>
    /// <exception cref="OperationCanceledException">The request or the response read is canceled.</exception>
    /// <exception cref="ObjectDisposedException">The transport is disposed: this client created it and was disposed, or the caller disposed its own.</exception>
    public ValueTask<ResourceCommandObservation> DeleteCommandAsync(ResourceCommand command, CancellationToken cancellationToken = default) =>
        SendAsync(command, HttpMethod.Delete, cancellationToken);

    /// <summary>Disposes the transport when the client created it; a caller-owned transport is left open.</summary>
    public void Dispose()
    {
        if (_ownsTransport)
        {
            _transport.Dispose();
        }
    }

    private static void ValidateArguments(Uri controlPlaneAddress, string bearerToken)
    {
        Uri.ThrowIfNotEndpoint(controlPlaneAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(bearerToken);
        if (controlPlaneAddress.Scheme != Uri.UriSchemeHttp && controlPlaneAddress.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("The database control-plane address must use HTTP or HTTPS.", nameof(controlPlaneAddress));
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
