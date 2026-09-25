using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Cli.Internal;

internal sealed class DeviceLogin
{
    private readonly HttpClient _http;
    private readonly TextWriter _output;
    private readonly TextWriter _error;
    private readonly string _homeDirectory;
    private readonly Func<TimeSpan, CancellationToken, Task>? _delay;
    private readonly TimeProvider _time;

    /// <summary>Initializes a new instance of the <see cref="DeviceLogin"/> class.</summary>
    /// <param name="http">The HTTP client used for OIDC discovery, device authorization, and token requests.</param>
    /// <param name="output">The writer for standard output.</param>
    /// <param name="error">The writer for standard error, where the verification prompt is written.</param>
    /// <param name="homeDirectory">The user's home directory, where the credential is stored.</param>
    /// <param name="delay">The optional delay function used between token polls; defaults to <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</param>
    /// <param name="timeProvider">The optional time source; defaults to <see cref="TimeProvider.System"/>.</param>
    public DeviceLogin(
        HttpClient http, TextWriter output, TextWriter error, string homeDirectory,
        Func<TimeSpan, CancellationToken, Task>? delay = null, TimeProvider? timeProvider = null)
    {
        _http = http;
        _output = output;
        _error = error;
        _homeDirectory = homeDirectory;
        _delay = delay;
        _time = timeProvider ?? TimeProvider.System;
    }

    internal async Task<int> ExecuteAsync(Arguments args, CancellationToken cancellationToken = default)
    {
        string issuerText = args.TakeValue("--issuer") ?? throw new CliException("login requires --issuer <url>.");
        string clientId = args.TakeValue("--client-id") ?? throw new CliException("login requires --client-id <id>.");
        string? clientSecret = args.TakeValue("--client-secret");
        string? audience = args.TakeValue("--audience");
        string scope = args.TakeValue("--scope") ?? "openid";
        bool print = args.TakeFlag("--print");
        args.RequireEmpty();
        Uri issuer = SecureEndpoint(issuerText);
        string issuerUrl = issuer.AbsoluteUri.TrimEnd('/');
        using HttpResponseMessage discoveryResponse = await _http.GetAsync(
            issuerUrl + "/.well-known/openid-configuration", cancellationToken).ConfigureAwait(false);
        RequireSuccess(discoveryResponse, "OIDC discovery");
        DiscoveryDocument discovery = await ReadAsync(discoveryResponse,
            LoginJsonContext.Default.DiscoveryDocument, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(discovery.DeviceAuthorizationEndpoint))
        {
            throw new CliException($"IdentityHub at {issuerUrl} does not expose the device flow (at HEAD it is enabled only for a Local hub bound to loopback — IdentityEndpointService.cs:89-90)");
        }
        if (discovery.Issuer is not null && discovery.Issuer.TrimEnd('/') != issuerUrl)
        {
            throw new CliException("OIDC discovery issuer does not match --issuer.");
        }
        Uri authorizationEndpoint = SecureEndpoint(discovery.DeviceAuthorizationEndpoint);
        Uri tokenEndpoint = SecureEndpoint(discovery.TokenEndpoint ?? throw new CliException("OIDC discovery has no token_endpoint."));
        var authorizationForm = new Dictionary<string, string> { ["client_id"] = clientId, ["scope"] = scope };
        if (clientSecret is not null)
        {
            authorizationForm["client_secret"] = clientSecret;
        }
        if (audience is not null)
        {
            authorizationForm["audience"] = audience;
        }
        using var authorizationContent = new FormUrlEncodedContent(authorizationForm);
        using HttpResponseMessage authorizationResponse = await _http.PostAsync(
            authorizationEndpoint, authorizationContent, cancellationToken).ConfigureAwait(false);
        RequireSuccess(authorizationResponse, "Device authorization");
        DeviceAuthorizationDocument device = await ReadAsync(authorizationResponse,
            LoginJsonContext.Default.DeviceAuthorizationDocument, cancellationToken).ConfigureAwait(false);
        string? verification = device.VerificationUriComplete ?? device.VerificationUri;
        if (string.IsNullOrWhiteSpace(device.DeviceCode) || string.IsNullOrWhiteSpace(device.UserCode) ||
            !Uri.TryCreate(verification, UriKind.Absolute, out Uri? verificationUri) ||
            (verificationUri.Scheme != Uri.UriSchemeHttp && verificationUri.Scheme != Uri.UriSchemeHttps) ||
            device.ExpiresIn <= 0 || device.Interval <= 0)
        {
            throw new CliException("IdentityHub returned an incomplete device authorization response.");
        }
        _error.WriteLine($"Open {verification} and approve user code {device.UserCode}.");
        DateTimeOffset deadline = _time.GetUtcNow().AddSeconds(device.ExpiresIn);
        TimeSpan interval = TimeSpan.FromSeconds(device.Interval);
        var tokenForm = new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
            ["device_code"] = device.DeviceCode,
            ["client_id"] = clientId
        };
        if (clientSecret is not null)
        {
            tokenForm["client_secret"] = clientSecret;
        }

        while (_time.GetUtcNow() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TimeSpan remaining = deadline - _time.GetUtcNow();
            await (_delay ?? Task.Delay)(interval < remaining ? interval : remaining, cancellationToken).ConfigureAwait(false);
            if (_time.GetUtcNow() >= deadline)
            {
                break;
            }
            using var tokenContent = new FormUrlEncodedContent(tokenForm);
            using HttpResponseMessage tokenResponse = await _http.PostAsync(tokenEndpoint,
                tokenContent, cancellationToken).ConfigureAwait(false);
            TokenDocument token = await ReadAsync(tokenResponse, LoginJsonContext.Default.TokenDocument,
                cancellationToken).ConfigureAwait(false);
            if (_time.GetUtcNow() >= deadline)
            {
                break;
            }
            if (token.Error is not null)
            {
                switch (token.Error)
                {
                    case "authorization_pending":
                        continue;
                    case "slow_down":
                        interval += TimeSpan.FromSeconds(5);
                        continue;
                    case "expired_token":
                    case "access_denied":
                        throw new CliException("Device authorization ended: " + token.Error + ".");
                    default:
                        throw new CliException("IdentityHub rejected the device token request. Check the client registration.");
                }
            }
            RequireSuccess(tokenResponse, "Device token");
            if (string.IsNullOrWhiteSpace(token.AccessToken) || string.IsNullOrWhiteSpace(token.TokenType) || token.ExpiresIn <= 0)
            {
                throw new CliException("IdentityHub returned an incomplete token response.");
            }
            if (print)
            {
                _output.WriteLine(token.AccessToken);
            }
            else
            {
                // Proposed credential contract for item 27 (#972); the design specifies no login store.
                string path = Path.Combine(_homeDirectory, ".cohesion", "credentials", issuer.IdnHost.Replace(':', '_') + ".json");
                var credential = new CredentialDocument
                {
                    AccessToken = token.AccessToken, TokenType = token.TokenType,
                    ExpiresAt = _time.GetUtcNow().AddSeconds(token.ExpiresIn), Issuer = issuerUrl
                };
                byte[] json = JsonSerializer.SerializeToUtf8Bytes(credential, LoginJsonContext.Default.CredentialDocument);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ProtectedFile.Write(path, json);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(json);
                }
                _output.WriteLine("IdentityHub credential saved for the later gateway token bridge.");
            }
            return 0;
        }
        throw new CliException("Device authorization ended: expired_token.");
    }

    private static Uri SecureEndpoint(string value)
    {
        Uri endpoint = HttpEndpoint.Parse(value);
        if (endpoint.Scheme != Uri.UriSchemeHttps && !endpoint.IsLoopback)
        {
            throw new CliException("IdentityHub credentials require HTTPS, except for a loopback Local hub.");
        }
        return endpoint;
    }

    private static void RequireSuccess(HttpResponseMessage response, string operation)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new CliException($"{operation} received HTTP {(int)response.StatusCode}.");
        }
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken = default)
    {
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync(stream, typeInfo, cancellationToken).ConfigureAwait(false)
            ?? throw new CliException("Empty IdentityHub response.");
    }
}
