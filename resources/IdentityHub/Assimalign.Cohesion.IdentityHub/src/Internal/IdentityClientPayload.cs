using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.IdentityHub.Internal;

/// <summary>
/// The stored layout of an app registration: what <see cref="IdentityClientCodec"/> writes as a
/// record's payload. The registration's identifier is the record's key and is not repeated here.
/// </summary>
/// <remarks>
/// Every member has an explicit name and enumerations are written as fixed strings, so renaming
/// a type or a member in code never changes a stored record. A member the layout does not know
/// is refused when a record is read.
/// </remarks>
internal sealed class IdentityClientPayload
{
    [JsonPropertyName("v")]
    public int Layout { get; set; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("grants")]
    public List<string>? Grants { get; set; }

    [JsonPropertyName("accessTokenLifetime")]
    public TimeSpan AccessTokenLifetime { get; set; }

    [JsonPropertyName("redirects")]
    public List<Redirect>? Redirects { get; set; }

    [JsonPropertyName("credentials")]
    public List<Credential>? Credentials { get; set; }

    [JsonPropertyName("audiences")]
    public List<string>? Audiences { get; set; }

    [JsonPropertyName("scopes")]
    public List<Scope>? Scopes { get; set; }

    [JsonPropertyName("roles")]
    public List<Role>? Roles { get; set; }

    [JsonPropertyName("permissions")]
    public List<Permission>? Permissions { get; set; }

    internal sealed class Redirect
    {
        [JsonPropertyName("uri")]
        public string? Uri { get; set; }

        [JsonPropertyName("platform")]
        public string? Platform { get; set; }
    }

    /// <summary>
    /// One credential. <see cref="Kind"/> says which of the remaining members are written: a
    /// secret's verifier, a certificate, or a federated credential's names.
    /// </summary>
    internal sealed class Credential
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("kind")]
        public string? Kind { get; set; }

        [JsonPropertyName("displayName")]
        public string? DisplayName { get; set; }

        [JsonPropertyName("notBefore")]
        public DateTimeOffset? NotBefore { get; set; }

        [JsonPropertyName("expiresAt")]
        public DateTimeOffset? ExpiresAt { get; set; }

        [JsonPropertyName("salt")]
        public byte[]? Salt { get; set; }

        [JsonPropertyName("verifier")]
        public byte[]? Verifier { get; set; }

        [JsonPropertyName("iterations")]
        public int? Iterations { get; set; }

        [JsonPropertyName("certificate")]
        public byte[]? Certificate { get; set; }

        [JsonPropertyName("issuer")]
        public string? Issuer { get; set; }

        [JsonPropertyName("subject")]
        public string? Subject { get; set; }

        [JsonPropertyName("audiences")]
        public List<string>? Audiences { get; set; }
    }

    internal sealed class Scope
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("displayName")]
        public string? DisplayName { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }
    }

    internal sealed class Role
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("members")]
        public List<string>? Members { get; set; }

        [JsonPropertyName("displayName")]
        public string? DisplayName { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }
    }

    internal sealed class Permission
    {
        [JsonPropertyName("resource")]
        public string? Resource { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("kind")]
        public string? Kind { get; set; }
    }
}
