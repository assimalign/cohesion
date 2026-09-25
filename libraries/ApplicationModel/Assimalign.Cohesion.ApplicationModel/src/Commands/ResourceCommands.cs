using System;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

using Assimalign.Cohesion.ApplicationModel.Internal;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>Creates immutable declarative commands using explicit JSON metadata.</summary>
public static class ResourceCommands
{
    /// <summary>Creates a command with a deterministic identity and canonical JSON payload.</summary>
    /// <typeparam name="TPayload">The source-generated payload contract.</typeparam>
    /// <param name="kind">The target's stable command kind.</param>
    /// <param name="key">The nonblank provider ownership key.</param>
    /// <param name="target">The graph's target resource instance.</param>
    /// <param name="owner">The application declaring the command.</param>
    /// <param name="payload">The desired command payload.</param>
    /// <param name="typeInfo">Source-generated JSON serialization metadata.</param>
    /// <param name="optional">Whether rejection may allow dependents to start.</param>
    /// <returns>The immutable declaration; registration remains the builder's responsibility.</returns>
    /// <exception cref="ArgumentException">The kind, key, or owner is blank.</exception>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="JsonException">The supplied metadata produces an invalid JSON payload.</exception>
    public static IResourceCommand Create<TPayload>(
        string kind,
        string key,
        IApplicationResource target,
        ApplicationName owner,
        TPayload payload,
        JsonTypeInfo<TPayload> typeInfo,
        bool optional = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(typeInfo);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner.ToString(), nameof(owner));
        byte[] serialized = JsonSerializer.SerializeToUtf8Bytes(payload, typeInfo);
        byte[] canonical = DeclarativeResourceCommand.Canonicalize(serialized);
        return new DeclarativeResourceCommand(
            DeclarativeResourceCommand.CreateId(kind, target, canonical), kind, key, target, owner, canonical, optional);
    }
}
