using System;

namespace Assimalign.Cohesion.IdentityHub;

/// <summary>
/// One entry a record lists under an index of its record set: the index's name, the value the
/// record is found by, and whether that value belongs to this record alone.
/// </summary>
/// <remarks>
/// <para>
/// A record with a <b>unique</b> entry is the only live record in its set that lists that name
/// and value as unique: a store refuses a write that would give two records the same one. A
/// user name within a directory and the user code of a device authorization are unique entries.
/// </para>
/// <para>
/// A record with an entry that is not unique shares it freely: any number of records list the
/// same name and value, and a query by that index returns all of them. The subject a session
/// belongs to is such an entry.
/// </para>
/// <para>
/// A record may list one index more than once with different values (a user's addresses), and
/// every entry of one index in one record agrees on whether it is unique. Index values follow
/// the key grammar and, like keys, are never secrets. <see langword="default"/> names no index
/// and <see cref="IdentityHubRecord"/> refuses it.
/// </para>
/// </remarks>
public readonly record struct IdentityHubRecordIndex
{
    /// <summary>
    /// Initializes an index entry.
    /// </summary>
    /// <param name="name">The index's name.</param>
    /// <param name="value">The value the record is found by under the index.</param>
    /// <param name="isUnique">
    /// <see langword="true"/> when no other live record in the set may list the same name and
    /// value as unique.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="name"/> or <paramref name="value"/> is <see langword="default"/>.
    /// </exception>
    public IdentityHubRecordIndex(IdentityHubRecordIndexName name, IdentityHubRecordKey value, bool isUnique = false)
    {
        if (name.Value is null)
        {
            throw new ArgumentException(ErrorMessages.RecordIndexNameRequired, nameof(name));
        }

        if (value.Value is null)
        {
            throw new ArgumentException(ErrorMessages.RecordIndexValueRequired, nameof(value));
        }

        Name = name;
        Value = value;
        IsUnique = isUnique;
    }

    /// <summary>
    /// Gets the index's name.
    /// </summary>
    public IdentityHubRecordIndexName Name { get; }

    /// <summary>
    /// Gets the value the record is found by under the index.
    /// </summary>
    public IdentityHubRecordKey Value { get; }

    /// <summary>
    /// Gets a value indicating whether no other live record in the set may list the same name
    /// and value as unique.
    /// </summary>
    public bool IsUnique { get; }
}
