using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.IdentityHub.Internal;

/// <summary>
/// The stored layout of a group: what <see cref="IdentityGroupCodec"/> writes as a record's
/// payload. The group's identifier is the record's key and is not repeated here.
/// </summary>
/// <remarks>
/// Every member has an explicit name, so renaming a member in code never changes a stored
/// record. A member the layout does not know is refused when a record is read.
/// </remarks>
internal sealed class IdentityGroupPayload
{
    [JsonPropertyName("v")]
    public int Layout { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }
}
