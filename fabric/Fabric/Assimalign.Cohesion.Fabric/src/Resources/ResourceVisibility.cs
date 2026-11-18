using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.Fabric;

/// <summary>
/// Whether a resource is addressable on its own or only through the composite that owns it.
/// </summary>
public enum ResourceVisibility
{
    /// <summary>
    /// The resource is addressable by the application's principals, subject to their access.
    /// </summary>
    [JsonStringEnumMemberName("public")]
    Public,

    /// <summary>
    /// The resource is a composite's private child. Only the owner's reconcile writes it, and a
    /// reader without private access sees it only by its role in the owner's status.
    /// </summary>
    [JsonStringEnumMemberName("private")]
    Private,
}
