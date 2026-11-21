using System;

namespace Assimalign.Cohesion.IdentityHub;

/// <summary>
/// The result of a conditional write to an <see cref="IIdentityHubStore"/>: whether the record
/// was stored, its new version when it was, and the index that refused it when one did.
/// </summary>
/// <remarks>
/// <see langword="default"/> reports <see cref="IdentityHubWriteStatus.None"/>, which is not a
/// written record, so a result a store forgot to set never reads as success.
/// </remarks>
public readonly record struct IdentityHubWriteResult
{
    private IdentityHubWriteResult(
        IdentityHubWriteStatus status,
        IdentityHubRecordVersion version,
        IdentityHubRecordIndexName conflictingIndex)
    {
        Status = status;
        Version = version;
        ConflictingIndex = conflictingIndex;
    }

    /// <summary>
    /// Gets the result of an add that was refused because a live record already holds the key.
    /// </summary>
    public static IdentityHubWriteResult KeyConflict => new(IdentityHubWriteStatus.KeyConflict, default, default);

    /// <summary>
    /// Gets the result of a replace that was refused because no live record holds the key, or
    /// the record's version is not the expected one.
    /// </summary>
    public static IdentityHubWriteResult VersionConflict => new(IdentityHubWriteStatus.VersionConflict, default, default);

    /// <summary>
    /// Gets the outcome of the write.
    /// </summary>
    public IdentityHubWriteStatus Status { get; }

    /// <summary>
    /// Gets the version the store assigned to the record, or <see langword="default"/> when the
    /// record was not written.
    /// </summary>
    public IdentityHubRecordVersion Version { get; }

    /// <summary>
    /// Gets the unique index another live record already lists, or <see langword="default"/>
    /// when the status is not <see cref="IdentityHubWriteStatus.IndexConflict"/>.
    /// </summary>
    public IdentityHubRecordIndexName ConflictingIndex { get; }

    /// <summary>
    /// Gets a value indicating whether the record was stored.
    /// </summary>
    public bool IsWritten => Status == IdentityHubWriteStatus.Written;

    /// <summary>
    /// Creates the result of a write that stored the record.
    /// </summary>
    /// <param name="version">The version the store assigned to the record.</param>
    /// <returns>A written result carrying <paramref name="version"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="version"/> is <see langword="default"/>.</exception>
    public static IdentityHubWriteResult Written(IdentityHubRecordVersion version)
    {
        if (version.Value is null)
        {
            throw new ArgumentException(ErrorMessages.RecordVersionRequired, nameof(version));
        }

        return new IdentityHubWriteResult(IdentityHubWriteStatus.Written, version, default);
    }

    /// <summary>
    /// Creates the result of a write that was refused because another live record lists one of
    /// the record's unique index entries.
    /// </summary>
    /// <param name="index">The unique index whose value is already taken.</param>
    /// <returns>An index-conflict result naming <paramref name="index"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="index"/> is <see langword="default"/>.</exception>
    public static IdentityHubWriteResult IndexConflict(IdentityHubRecordIndexName index)
    {
        if (index.Value is null)
        {
            throw new ArgumentException(ErrorMessages.RecordIndexNameRequired, nameof(index));
        }

        return new IdentityHubWriteResult(IdentityHubWriteStatus.IndexConflict, default, index);
    }
}
