using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Content.Bmff;

/// <summary>Builds immutable registries using explicit, AOT-safe factory delegates.</summary>
public sealed class BmffBoxRegistryBuilder
{
    internal Dictionary<BmffBoxType, Func<BmffBoxHeader, BmffBox>> Factories { get; } = new();
    internal HashSet<BmffBoxType> Unsupported { get; } = new();
    internal Dictionary<BmffBoxType, Func<BmffBoxHeader, BmffBox>> SampleEntries { get; } = new();
    internal Dictionary<BmffBoxType, Func<BmffBox, (Action<IBmffRelocationContext> Apply, Action Restore)>> Relocations { get; } = new();

    /// <summary>Registers or replaces a transactional relocation rule for a profile box.</summary>
    /// <param name="type">The box FourCC.</param>
    /// <param name="capture">Captures original fields without mutation and returns apply and restore callbacks.
    /// Apply may run repeatedly against the same checkpoint; restore must undo all its changes on failure.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException">The capture callback is null.</exception>
    public BmffBoxRegistryBuilder RegisterRelocation(BmffBoxType type,
        Func<BmffBox, (Action<IBmffRelocationContext> Apply, Action Restore)> capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        Relocations[type] = capture;
        return this;
    }

    /// <summary>Registers a sample-entry factory for stsd and standalone entry reading.</summary>
    /// <param name="type">The sample-entry FourCC.</param>
    /// <param name="factory">A factory receiving the validated wire header.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException">The factory is null.</exception>
    public BmffBoxRegistryBuilder RegisterSampleEntry(BmffBoxType type, Func<BmffBoxHeader, BmffBox> factory)
    {
        Register(type, factory);
        SampleEntries[type] = factory;
        return this;
    }

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
    public BmffBoxRegistry Build() => new BmffBoxRegistry(Factories, Unsupported, SampleEntries, Relocations);
}
