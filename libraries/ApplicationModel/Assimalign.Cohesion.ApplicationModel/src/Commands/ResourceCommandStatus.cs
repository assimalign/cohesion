using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>The observed outcome of applying a declared resource command.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ResourceCommandStatus>))]
public enum ResourceCommandStatus
{
    /// <summary>The target accepted and applied the declaration.</summary>
    Applied,
    /// <summary>The target refused the declaration; the observation carries its detail.</summary>
    Rejected,
}
