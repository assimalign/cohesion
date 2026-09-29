using System.Text.Json.Serialization;

using Assimalign.Cohesion.ApplicationModel.Internal;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Locates the resource's default control plane within its declared endpoints.
/// </summary>
/// <param name="Endpoint">
/// The manifest endpoint that serves the control plane, or an empty string for a legacy plan
/// that omitted control-plane facts.
/// </param>
/// <param name="Path">
/// The path prefix under which the control plane is served, or an empty string for a legacy plan
/// that omitted control-plane facts.
/// </param>
[JsonConverter(typeof(ControlPlaneSpecJsonConverter))]
public sealed record ControlPlaneSpec(
    string Endpoint = "",
    string Path = "");
