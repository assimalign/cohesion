using System;

using Assimalign.Cohesion.Fabric.Internal;

namespace Assimalign.Cohesion.Fabric;

/// <summary>
/// A resource type, <c>{Namespace}/{type}</c>, such as <c>Cohesion.Workloads/workloads</c> or
/// <c>Kubernetes.Networking/networkPolicies</c>.
/// </summary>
/// <remarks>
/// The namespace names the provider that owns the type. Cohesion's portable types use the
/// <c>Cohesion.</c> prefix; a platform-native type (escape-hatch tier T2) uses its platform's own
/// namespace.
/// </remarks>
public partial struct ResourceTypeName
{
    /// <summary>
    /// Gets the provider namespace, the part before the slash.
    /// </summary>
    public string Namespace => Value is null ? string.Empty : Value[..Value.IndexOf('/')];

    /// <summary>
    /// Gets the type, the part after the slash.
    /// </summary>
    public string Type => Value is null ? string.Empty : Value[(Value.IndexOf('/') + 1)..];

    /// <summary>
    /// Determines whether <paramref name="value"/> is a valid resource type name.
    /// </summary>
    /// <param name="value">The candidate type name.</param>
    /// <param name="message">The reason the name is refused, or <see langword="null"/> when it is valid.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> has a valid namespace and type.</returns>
    public static partial bool IsValid(string value, out string? message)
    {
        var slash = value is null ? -1 : value.IndexOf('/');
        if (slash <= 0
            || !FabricGrammar.IsProviderNamespace(value.AsSpan(0, slash))
            || !FabricGrammar.IsTypeSegment(value.AsSpan(slash + 1)))
        {
            message = ErrorMessages.ResourceTypeNameInvalid;
            return false;
        }

        message = null;
        return true;
    }
}
