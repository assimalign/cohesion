using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

using Assimalign.Cohesion.ApplicationModel.Internal;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Describes another resource referenced by this resource.
/// </summary>
public sealed record ResourceManifestReference
{
    /// <summary>
    /// Gets the referenced resource name.
    /// </summary>
    [JsonConverter(typeof(ResourceNameJsonConverter))]
    public ResourceName Resource { get; init; }

    /// <summary>
    /// Gets the application that owns the referenced resource.
    /// </summary>
    [JsonConverter(typeof(ApplicationNameJsonConverter))]
    public ApplicationName Application { get; init; }

    /// <summary>
    /// Gets the endpoint names consumed from the referenced resource.
    /// </summary>
    public IReadOnlyList<string> Endpoints { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Gets a value indicating whether an unresolved reference may be skipped.
    /// </summary>
    public bool Optional { get; init; }

    /// <summary>
    /// Gets the project or manifest-package identity that supplied the reference.
    /// </summary>
    public string Manifest { get; init; } = string.Empty;
}
