using System;

namespace Assimalign.Cohesion.Database.Storage;

/// <summary>
/// Represents a single named value inside a <see cref="StorageTuple"/>.
/// </summary>
public readonly struct StorageTupleField
{
    /// <summary>
    /// Initializes a new <see cref="StorageTupleField"/>.
    /// </summary>
    /// <param name="name">Field name. Must not be null or empty.</param>
    /// <param name="value">Field value bytes.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="name"/> is null or empty.</exception>
    public StorageTupleField(string name, ReadOnlyMemory<byte> value)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Tuple field name cannot be null or empty.", nameof(name));
        }

        Name = name;
        Value = value;
    }

    /// <summary>
    /// Gets the tuple field name.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the tuple field value bytes.
    /// </summary>
    public ReadOnlyMemory<byte> Value { get; }
}
