using System;
using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.Cli;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, WriteIndented = true)]
[JsonSerializable(typeof(DiscoveryDocument))]
[JsonSerializable(typeof(DeviceAuthorizationDocument))]
[JsonSerializable(typeof(TokenDocument))]
[JsonSerializable(typeof(CredentialDocument))]
internal sealed partial class LoginJsonContext : JsonSerializerContext;

internal sealed class DiscoveryDocument
{
    public string? Issuer { get; set; }
    public string? DeviceAuthorizationEndpoint { get; set; }
    public string? TokenEndpoint { get; set; }
}

internal sealed class DeviceAuthorizationDocument
{
    public string? DeviceCode { get; set; }
    public string? UserCode { get; set; }
    public string? VerificationUri { get; set; }
    public string? VerificationUriComplete { get; set; }
    public int ExpiresIn { get; set; }
    public int Interval { get; set; } = 5;
}

internal sealed class TokenDocument
{
    public string? AccessToken { get; set; }
    public string? TokenType { get; set; }
    public int ExpiresIn { get; set; }
    public string? Error { get; set; }
}

internal sealed class CredentialDocument
{
    public required string AccessToken { get; set; }
    public required string TokenType { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public required string Issuer { get; set; }
}
