using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.Networking;

/// <summary>
/// Who may reach an exposed port.
/// </summary>
public enum ExposureVisibility
{
    /// <summary>
    /// Other resources in the same environment.
    /// </summary>
    [JsonStringEnumMemberName("internal")]
    Internal,

    /// <summary>
    /// Clients outside the environment.
    /// </summary>
    [JsonStringEnumMemberName("public")]
    Public,
}
