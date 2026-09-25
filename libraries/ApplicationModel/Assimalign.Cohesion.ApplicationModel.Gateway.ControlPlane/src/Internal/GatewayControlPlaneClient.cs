using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using ResourceCommand = Assimalign.Cohesion.Hosting.Resources.ResourceCommand;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.ControlPlane.Internal;

internal sealed class GatewayControlPlaneClient : IAuthenticatedControlPlaneClient
{
    public ValueTask<ResourceCommandResult> ApplyCommandAsync(
        Uri address, ResourceName resource, string bearerToken, ResourceCommand command,
        CancellationToken cancellationToken = default) =>
        SendCommandAsync(address, resource, bearerToken, command, HttpMethod.Put, cancellationToken);

    public ValueTask<ResourceCommandResult> DeleteCommandAsync(
        Uri address, ResourceName resource, string bearerToken, ResourceCommand command,
        CancellationToken cancellationToken = default) =>
        SendCommandAsync(address, resource, bearerToken, command, HttpMethod.Delete, cancellationToken);

    private static async ValueTask<ResourceCommandResult> SendCommandAsync(
        Uri address, ResourceName resource, string bearerToken, ResourceCommand command,
        HttpMethod method, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(bearerToken);
        ValidateTransport(address);
        string path = "/cohesion/v1/resources/" + Uri.EscapeDataString(resource.ToString()) +
            "/commands/" + Uri.EscapeDataString(command.Id);
        Uri endpoint = new UriBuilder(address.Scheme, address.Host, address.Port, path).Uri;
        using var request = new HttpRequestMessage(method, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        if (method == HttpMethod.Put)
        {
            request.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(
                new ControlPlaneCommandRequest
                {
                    Id = command.Id, Kind = command.Kind, Owner = command.Owner,
                    Key = command.Key, Payload = command.Payload.ToArray(),
                }, ControlPlaneJsonContext.Default.ControlPlaneCommandRequest));
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        using HttpResponseMessage response = await _client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await response.Content.LoadIntoBufferAsync(MaximumApplicationExportBytes, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
        {
            return new(ResourceCommandStatus.Applied, "Owned command removed by the peer gateway.");
        }
        byte[] body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (body.Length == 0)
        {
            return new(ResourceCommandStatus.Rejected, $"Peer returned HTTP {(int)response.StatusCode} without command detail.");
        }
        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;
        string detail = root.TryGetProperty("detail", out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : root.TryGetProperty("error", out value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()! : $"Peer returned HTTP {(int)response.StatusCode}.";
        bool applied = response.IsSuccessStatusCode && root.TryGetProperty("status", out value) &&
            value.GetString() == "Applied";
        if (applied &&
            (!root.TryGetProperty("id", out value) || value.GetString() != command.Id ||
             !root.TryGetProperty("owner", out value) || value.GetString() != command.Owner ||
             !root.TryGetProperty("kind", out value) || value.GetString() != command.Kind ||
             !root.TryGetProperty("key", out value) || value.GetString() != command.Key))
        {
            return new(ResourceCommandStatus.Rejected, "Peer command observation does not match the submitted declaration.");
        }
        byte[]? result = applied && root.TryGetProperty("result", out value) && value.ValueKind == JsonValueKind.String
            ? value.GetBytesFromBase64() : null;
        return new(applied ? ResourceCommandStatus.Applied : ResourceCommandStatus.Rejected,
            detail, result ?? ReadOnlyMemory<byte>.Empty);
    }

    private const string ApplicationPath = "/cohesion/v1/application";
    private const long MaximumApplicationExportBytes = 16 * 1024 * 1024;

    private static readonly HttpClient _client = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
    });

    private readonly string? _bearerToken;
    private readonly TrustedIssuer? _trustedIssuer;

    public GatewayControlPlaneClient()
    {
    }

    public GatewayControlPlaneClient(string bearerToken, TrustedIssuer trustedIssuer)
    {
        _bearerToken = bearerToken;
        _trustedIssuer = trustedIssuer;
    }

    public ValueTask<ApplicationExportDocument> GetApplicationAsync(
        Uri address,
        CancellationToken cancellationToken = default)
    {
        if (_bearerToken is null || _trustedIssuer is null)
        {
            throw new InvalidOperationException(
                "This control-plane client requires application gateway credential context. " +
                "Use the fixed-credential factory for direct resolver calls.");
        }

        return GetApplicationAsync(
            address,
            _bearerToken,
            new[] { _trustedIssuer },
            cancellationToken);
    }

    public async ValueTask<ApplicationExportDocument> GetApplicationAsync(
        Uri address,
        string bearerToken,
        IReadOnlyList<TrustedIssuer> trustedIssuers,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentException.ThrowIfNullOrWhiteSpace(bearerToken);
        ArgumentNullException.ThrowIfNull(trustedIssuers);
        ValidateTransport(address);

        Uri endpoint = new UriBuilder(
            address.Scheme,
            address.Host,
            address.IsDefaultPort ? -1 : address.Port,
            ApplicationPath).Uri;
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        using HttpResponseMessage response = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long contentLength &&
            contentLength > MaximumApplicationExportBytes)
        {
            throw new InvalidDataException(
                $"Application export response is larger than {MaximumApplicationExportBytes} bytes.");
        }

        await response.Content
            .LoadIntoBufferAsync(MaximumApplicationExportBytes, cancellationToken)
            .ConfigureAwait(false);

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        ApplicationExportDocument document = await ApplicationExportDocument.LoadAsync(
                stream,
                cancellationToken)
            .ConfigureAwait(false);
        VerifyTrustKey(document, trustedIssuers);
        return document;
    }

    private static void ValidateTransport(Uri address)
    {
        if (!address.IsAbsoluteUri ||
            (!string.Equals(address.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
             !(string.Equals(address.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
               address.IsLoopback)))
        {
            throw new InvalidOperationException(
                $"Control-plane bearer credentials require HTTPS or loopback HTTP; '{address}' is not allowed.");
        }
    }

    private static void VerifyTrustKey(
        ApplicationExportDocument document,
        IReadOnlyList<TrustedIssuer> trustedIssuers)
    {
        TrustedIssuer? expected = null;
        for (int index = 0; index < trustedIssuers.Count; index++)
        {
            TrustedIssuer issuer = trustedIssuers[index]
                ?? throw new ArgumentException(
                    $"Trusted issuer at index {index} is null.",
                    nameof(trustedIssuers));
            if (string.Equals(issuer.Issuer, document.Application, StringComparison.Ordinal))
            {
                expected = issuer;
                break;
            }
        }

        if (expected is null)
        {
            throw new InvalidDataException(
                $"Application export '{document.Application}' is not in the caller's trusted issuer set.");
        }

        JsonElement key = document.TrustKey ?? throw new InvalidDataException(
            $"Application export '{document.Application}' has no trustKey.");
        var observed = new TrustedIssuer(document.Application, key);
        if (!KeysEqual(expected.PublicKey, observed.PublicKey))
        {
            throw new InvalidDataException(
                $"Application export '{document.Application}' does not match its trusted public key.");
        }
    }

    private static bool KeysEqual(JsonElement left, JsonElement right)
    {
        string[] members = ["kty", "crv", "x", "y", "kid", "alg", "use"];
        for (int index = 0; index < members.Length; index++)
        {
            string member = members[index];
            if (!left.TryGetProperty(member, out JsonElement leftValue) ||
                !right.TryGetProperty(member, out JsonElement rightValue) ||
                !string.Equals(leftValue.GetString(), rightValue.GetString(), StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
