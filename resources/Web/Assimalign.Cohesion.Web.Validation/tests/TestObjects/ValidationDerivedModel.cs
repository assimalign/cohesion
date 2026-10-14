using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.Web.Validation.Tests.TestObjects;

[JsonDerivedType(typeof(ValidationDerivedModel), "derived")]
internal abstract class ValidationBaseModel;

[JsonDerivedType(typeof(ValidationLeafModel), "leaf")]
internal class ValidationDerivedModel : ValidationBaseModel
{
    [JsonPropertyName("display_name")]
    public string DisplayName { get; set; } = string.Empty;

    public ValidationDerivedModel? Next { get; set; }
}

internal sealed class ValidationLeafModel : ValidationDerivedModel
{
    [JsonPropertyName("tracking_code")]
    public string TrackingCode { get; set; } = string.Empty;
}

[JsonSerializable(typeof(ValidationBaseModel))]
internal sealed partial class ValidationDerivedJsonContext : JsonSerializerContext;
