using System;

namespace Assimalign.Cohesion.IdentityHub;

/// <summary>
/// The result of a write to an <see cref="IIdentityClientRegistry"/>: whether it took effect,
/// the registration's new version when it was stored, and the audience that refused it when
/// one did.
/// </summary>
/// <remarks>
/// <see langword="default"/> reports <see cref="IdentityClientWriteStatus.None"/>, which is not
/// an applied write, so a result a registry forgot to set never reads as success.
/// </remarks>
public readonly record struct IdentityClientWriteResult
{
    private IdentityClientWriteResult(
        IdentityClientWriteStatus status,
        IdentityHubRecordVersion version,
        IdentityClientAudience conflictingAudience)
    {
        Status = status;
        Version = version;
        ConflictingAudience = conflictingAudience;
    }

    /// <summary>
    /// Gets the result of a remove that removed the registration.
    /// </summary>
    public static IdentityClientWriteResult Removed => new(IdentityClientWriteStatus.Removed, default, default);

    /// <summary>
    /// Gets the result of a write that was refused because the identifier belongs to a
    /// registration defined in code.
    /// </summary>
    public static IdentityClientWriteResult Locked => new(IdentityClientWriteStatus.Locked, default, default);

    /// <summary>
    /// Gets the result of an add that was refused because a stored registration already holds
    /// the identifier.
    /// </summary>
    public static IdentityClientWriteResult IdConflict => new(IdentityClientWriteStatus.IdConflict, default, default);

    /// <summary>
    /// Gets the result of a replace or a remove that was refused because no stored registration
    /// holds the identifier, or its version is not the expected one.
    /// </summary>
    public static IdentityClientWriteResult VersionConflict => new(IdentityClientWriteStatus.VersionConflict, default, default);

    /// <summary>
    /// Gets the outcome of the write.
    /// </summary>
    public IdentityClientWriteStatus Status { get; }

    /// <summary>
    /// Gets the version the registry assigned to the stored registration, or
    /// <see langword="default"/> when the status is not <see cref="IdentityClientWriteStatus.Written"/>.
    /// </summary>
    public IdentityHubRecordVersion Version { get; }

    /// <summary>
    /// Gets the audience another registration already exposes, or <see langword="default"/>
    /// when the status is not <see cref="IdentityClientWriteStatus.AudienceConflict"/> or the
    /// registry could not tell which audience it was.
    /// </summary>
    public IdentityClientAudience ConflictingAudience { get; }

    /// <summary>
    /// Gets a value indicating whether the write took effect: the registration was stored or
    /// removed.
    /// </summary>
    public bool IsApplied => Status is IdentityClientWriteStatus.Written or IdentityClientWriteStatus.Removed;

    /// <summary>
    /// Creates the result of a write that stored the registration.
    /// </summary>
    /// <param name="version">The version the registry assigned to the stored registration.</param>
    /// <returns>A written result carrying <paramref name="version"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="version"/> is <see langword="default"/>.</exception>
    public static IdentityClientWriteResult Written(IdentityHubRecordVersion version)
    {
        if (version.Value is null)
        {
            throw new ArgumentException(ErrorMessages.RecordVersionRequired, nameof(version));
        }

        return new IdentityClientWriteResult(IdentityClientWriteStatus.Written, version, default);
    }

    /// <summary>
    /// Creates the result of a write that was refused because another registration already
    /// exposes one of the registration's audiences.
    /// </summary>
    /// <param name="audience">
    /// The audience that is taken, or <see langword="default"/> when the registry could not tell
    /// which one it was.
    /// </param>
    /// <returns>An audience-conflict result naming <paramref name="audience"/>.</returns>
    public static IdentityClientWriteResult AudienceConflict(IdentityClientAudience audience = default)
    {
        return new IdentityClientWriteResult(IdentityClientWriteStatus.AudienceConflict, default, audience);
    }
}
