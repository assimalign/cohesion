using System;
using System.Buffers;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel.Gateway.Internal;
using ResourceCommand = Assimalign.Cohesion.Hosting.Resources.ResourceCommand;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

// Deviates from the repo interface-first rule per the owner-approved BYO design (2026-09-25): the
// generic client is public so a gateway can re-register it after clearing CommandClients; its
// contract remains IGatewayResourceCommandClient and the class is sealed and stateless.

/// <summary>
/// Delivers declarative commands to any resource that serves the standard control-plane
/// <c>commands</c> route, without referencing an area client package.
/// </summary>
/// <remarks>
/// <para>
/// The client serves every manifest kind (<see cref="IGatewayResourceCommandClient.AnyKind"/>), so
/// the gateway uses it for any target that has no exact-kind registration. It is the default
/// entry in <see cref="ApplicationGatewayOptions.CommandClients"/>.
/// </para>
/// <para>
/// Apply sends <c>POST</c> and delete sends <c>DELETE</c> to <c>&lt;address&gt;/commands</c>, where
/// <c>&lt;address&gt;</c> is the observed endpoint plus the manifest control-plane path (a trailing
/// slash on that path is ignored). The request carries <c>Authorization: Bearer &lt;token&gt;</c>
/// and a <c>Content-Type: application/json</c> body
/// <c>{"id","kind","owner","key","payload"}</c> whose payload is base64 encoded. It sends no
/// <c>Accept</c> header, because every response type is read.
/// </para>
/// <para>
/// A success status maps to <see cref="ResourceCommandStatus.Applied"/> with the detail
/// <c>Applied</c> (apply) or <c>Deleted</c> (delete). An <c>application/json</c> response body may
/// override the observed <c>status</c> and <c>detail</c>; any status other than <c>Applied</c> or
/// <c>Deleted</c> maps to <see cref="ResourceCommandStatus.Rejected"/>. A non-success status is
/// always <see cref="ResourceCommandStatus.Rejected"/>, with the body's <c>detail</c> when present
/// and otherwise <c>Command '&lt;kind&gt;' was refused: HTTP &lt;code&gt; &lt;reason&gt;.</c>
/// </para>
/// <para>
/// Each call owns a fresh transport that applies the supplied server certificate validator and
/// never follows redirects or stores cookies; the transport is disposed when the call completes.
/// </para>
/// </remarks>
public sealed class ResourceControlPlaneCommandClient : IGatewayResourceCommandClient
{
    private const string commandsRoute = "/commands";
    private const string jsonMediaType = "application/json";
    private const string appliedStatus = "Applied";
    private const string deletedStatus = "Deleted";
    private const string rejectedStatus = "Rejected";

    /// <summary>
    /// Gets <see cref="IGatewayResourceCommandClient.AnyKind"/>: this client serves every manifest kind.
    /// </summary>
    public string ResourceKind => IGatewayResourceCommandClient.AnyKind;

    /// <summary>Applies one owner-scoped declaration by sending <c>POST &lt;address&gt;/commands</c>.</summary>
    /// <param name="address">The default control-plane endpoint including its manifest path.</param>
    /// <param name="bearerToken">The resource-scoped credential sent as the bearer token.</param>
    /// <param name="command">The desired command envelope.</param>
    /// <param name="serverCertificateValidator">Validates the target's TLS certificate against the application's transport anchors; <see langword="null"/> keeps the platform's default trust.</param>
    /// <param name="cancellationToken">Cancels delivery.</param>
    /// <returns>The observed outcome with the provider's detail.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="address"/>, <paramref name="bearerToken"/> or <paramref name="command"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="address"/> is not an HTTP(S) endpoint URI, or <paramref name="bearerToken"/>
    /// or a command identity field is empty or whitespace.
    /// </exception>
    /// <exception cref="HttpRequestException">The request could not be delivered.</exception>
    /// <exception cref="JsonException">The target returned a malformed JSON observation.</exception>
    /// <exception cref="InvalidOperationException">The target returned a JSON observation that is not an object.</exception>
    public ValueTask<ResourceCommandResult> ApplyAsync(
        Uri address, string bearerToken, ResourceCommand command,
        RemoteCertificateValidationCallback? serverCertificateValidator,
        CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, address, bearerToken, command, serverCertificateValidator, cancellationToken);

    /// <summary>Removes an owned declaration by sending <c>DELETE &lt;address&gt;/commands</c>.</summary>
    /// <param name="address">The default control-plane endpoint including its manifest path.</param>
    /// <param name="bearerToken">The resource-scoped credential sent as the bearer token.</param>
    /// <param name="command">The previously declared command.</param>
    /// <param name="serverCertificateValidator">Validates the target's TLS certificate against the application's transport anchors; <see langword="null"/> keeps the platform's default trust.</param>
    /// <param name="cancellationToken">Cancels delivery.</param>
    /// <returns>The observed deletion outcome.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="address"/>, <paramref name="bearerToken"/> or <paramref name="command"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="address"/> is not an HTTP(S) endpoint URI, or <paramref name="bearerToken"/>
    /// or a command identity field is empty or whitespace.
    /// </exception>
    /// <exception cref="HttpRequestException">The request could not be delivered.</exception>
    /// <exception cref="JsonException">The target returned a malformed JSON observation.</exception>
    /// <exception cref="InvalidOperationException">The target returned a JSON observation that is not an object.</exception>
    public ValueTask<ResourceCommandResult> DeleteAsync(
        Uri address, string bearerToken, ResourceCommand command,
        RemoteCertificateValidationCallback? serverCertificateValidator,
        CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, address, bearerToken, command, serverCertificateValidator, cancellationToken);

    private static async ValueTask<ResourceCommandResult> SendAsync(
        HttpMethod method, Uri address, string bearerToken, ResourceCommand command,
        RemoteCertificateValidationCallback? serverCertificateValidator,
        CancellationToken cancellationToken)
    {
        Uri.ThrowIfNotEndpoint(address);
        if (address.Scheme != Uri.UriSchemeHttp && address.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("The resource control-plane address must use HTTP or HTTPS.", nameof(address));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(bearerToken);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Id, nameof(command));
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Kind, nameof(command));
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Owner, nameof(command));
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Key, nameof(command));

        using HttpMessageInvoker transport = GatewayHttpTransport.Create(serverCertificateValidator);
        using var request = new HttpRequestMessage(method, CreateCommandUri(address));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        // No Accept header: any response type is read (only application/json bodies are parsed),
        // which matches the Database, IdentityHub and Rezolvr clients this replaced.
        request.Content = new ByteArrayContent(WriteEnvelope(command));
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(jsonMediaType);
        using HttpResponseMessage response = await transport.SendAsync(request, cancellationToken).ConfigureAwait(false);
        byte[] content = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

        bool success = response.IsSuccessStatusCode;
        string status = success ? method == HttpMethod.Delete ? deletedStatus : appliedStatus : rejectedStatus;
        string? detail = success
            ? null
            : $"Command '{command.Kind}' was refused: HTTP {(int)response.StatusCode} {response.ReasonPhrase}.";
        if (content.Length > 0 && response.Content.Headers.ContentType?.MediaType == jsonMediaType)
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

        // A refusal status is authoritative even when the body claims success.
        if (!success)
        {
            status = rejectedStatus;
        }
        return new ResourceCommandResult(
            status is appliedStatus or deletedStatus ? ResourceCommandStatus.Applied : ResourceCommandStatus.Rejected,
            detail ?? status);
    }

    private static Uri CreateCommandUri(Uri address) =>
        new UriBuilder(address) { Path = address.AbsolutePath.TrimEnd('/') + commandsRoute }.Uri;

    private static byte[] WriteEnvelope(ResourceCommand command)
    {
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
        return buffer.WrittenSpan.ToArray();
    }
}
