using System.Collections.Generic;
using System.Text.Json.Serialization.Metadata;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Adds declarative command authoring to a graph descriptor. Area-owned typed descriptors
/// implement this additive seam and retain the underlying resource's reference identity.
/// </summary>
public interface IResourceCommandDescriptor : IApplicationResourceDescriptor
{
    /// <summary>Gets commands declared against this descriptor in declaration order.</summary>
    IReadOnlyList<IResourceCommand> Commands { get; }

    /// <summary>Records a command for delivery after this target is running.</summary>
    /// <typeparam name="TPayload">The source-generated payload contract.</typeparam>
    /// <param name="kind">The stable wire command kind.</param>
    /// <param name="key">The nonblank provider ownership key.</param>
    /// <param name="payload">The command payload.</param>
    /// <param name="typeInfo">The source-generated payload serialization metadata.</param>
    /// <param name="optional">Whether a rejected command may allow dependents to start.</param>
    /// <returns>The recorded command.</returns>
    /// <exception cref="System.ArgumentException">The kind or key is blank.</exception>
    /// <exception cref="System.ArgumentNullException">The kind, key, or metadata is null.</exception>
    /// <exception cref="System.InvalidOperationException">The descriptor belongs to an immutable built model.</exception>
    /// <exception cref="System.Text.Json.JsonException">The supplied metadata produces an invalid JSON payload.</exception>
    IResourceCommand AddCommand<TPayload>(
        string kind,
        string key,
        TPayload payload,
        JsonTypeInfo<TPayload> typeInfo,
        bool optional = false);
}
