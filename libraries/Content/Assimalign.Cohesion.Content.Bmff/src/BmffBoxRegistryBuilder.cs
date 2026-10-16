using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Content.Bmff;

/// <summary>Builds immutable registries using explicit, AOT-safe factory delegates.</summary>
public sealed class BmffBoxRegistryBuilder
{
    internal Dictionary<BmffBoxType, Func<BmffBoxHeader, BmffBox>> Factories { get; } = new();
    internal HashSet<BmffBoxType> Unsupported { get; } = new();

    /// <summary>Adds or replaces a factory. Registering a type opts into its parser.</summary>
    /// <param name="type">The raw FourCC to register.</param>
    /// <param name="factory">A factory receiving the validated wire header.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException">The factory is null.</exception>
    public BmffBoxRegistryBuilder Register(BmffBoxType type, Func<BmffBoxHeader, BmffBox> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        Factories[type] = factory;
        Unsupported.Remove(type);
        return this;
    }

    /// <summary>Freezes a snapshot; later builder mutations cannot alter it.</summary>
    /// <returns>The registry snapshot.</returns>
    public BmffBoxRegistry Build() => new BmffBoxRegistry(Factories, Unsupported);
}
