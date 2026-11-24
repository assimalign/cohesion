using System;

namespace Assimalign.Cohesion.IdentityHub;

/// <summary>
/// The result of a write to an <see cref="IIdentityObjectRegistry{TObject, TId}"/>: whether it
/// took effect, and the object's new version when it was stored.
/// </summary>
/// <remarks>
/// <see langword="default"/> reports <see cref="IdentityObjectWriteStatus.None"/>, which is not
/// an applied write, so a result a registry forgot to set never reads as success.
/// </remarks>
public readonly record struct IdentityObjectWriteResult
{
    private IdentityObjectWriteResult(IdentityObjectWriteStatus status, IdentityHubRecordVersion version)
    {
        Status = status;
        Version = version;
    }

    /// <summary>
    /// Gets the result of a remove that removed the object.
    /// </summary>
    public static IdentityObjectWriteResult Removed => new(IdentityObjectWriteStatus.Removed, default);

    /// <summary>
    /// Gets the result of a write that was refused because the identifier belongs to an object
    /// defined in code.
    /// </summary>
    public static IdentityObjectWriteResult Locked => new(IdentityObjectWriteStatus.Locked, default);

    /// <summary>
    /// Gets the result of an add that was refused because a stored object already holds the
    /// identifier.
    /// </summary>
    public static IdentityObjectWriteResult IdConflict => new(IdentityObjectWriteStatus.IdConflict, default);

    /// <summary>
    /// Gets the result of a replace or a remove that was refused because no stored object holds
    /// the identifier, or its version is not the expected one.
    /// </summary>
    public static IdentityObjectWriteResult VersionConflict => new(IdentityObjectWriteStatus.VersionConflict, default);

    /// <summary>
    /// Gets the result of an add or a replace that was refused because another object of the
    /// same kind already holds the name.
    /// </summary>
    public static IdentityObjectWriteResult NameConflict => new(IdentityObjectWriteStatus.NameConflict, default);

    /// <summary>
    /// Gets the outcome of the write.
    /// </summary>
    public IdentityObjectWriteStatus Status { get; }

    /// <summary>
    /// Gets the version the registry assigned to the stored object, or <see langword="default"/>
    /// when the status is not <see cref="IdentityObjectWriteStatus.Written"/>.
    /// </summary>
    public IdentityHubRecordVersion Version { get; }

    /// <summary>
    /// Gets a value indicating whether the write took effect: the object was stored or removed.
    /// </summary>
    public bool IsApplied => Status is IdentityObjectWriteStatus.Written or IdentityObjectWriteStatus.Removed;

    /// <summary>
    /// Creates the result of a write that stored the object.
    /// </summary>
    /// <param name="version">The version the registry assigned to the stored object.</param>
    /// <returns>A written result carrying <paramref name="version"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="version"/> is <see langword="default"/>.</exception>
    public static IdentityObjectWriteResult Written(IdentityHubRecordVersion version)
    {
        if (version.Value is null)
        {
            throw new ArgumentException(ErrorMessages.RecordVersionRequired, nameof(version));
        }

        return new IdentityObjectWriteResult(IdentityObjectWriteStatus.Written, version);
    }
}
