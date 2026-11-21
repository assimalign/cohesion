using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.IdentityHub.Internal;

/// <summary>
/// The stored layout of a user: what <see cref="IdentityUserCodec"/> writes as a record's
/// payload. The user's identifier is the record's key and is not repeated here.
/// </summary>
/// <remarks>
/// Every member has an explicit name, so renaming a member in code never changes a stored
/// record. A member the layout does not know is refused when a record is read.
/// </remarks>
internal sealed class IdentityUserPayload
{
    [JsonPropertyName("v")]
    public int Layout { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("givenName")]
    public string? GivenName { get; set; }

    [JsonPropertyName("familyName")]
    public string? FamilyName { get; set; }

    [JsonPropertyName("email")]
    public string? Email { get; set; }

    [JsonPropertyName("emailVerified")]
    public bool? IsEmailVerified { get; set; }

    [JsonPropertyName("enabled")]
    public bool? IsEnabled { get; set; }
}
