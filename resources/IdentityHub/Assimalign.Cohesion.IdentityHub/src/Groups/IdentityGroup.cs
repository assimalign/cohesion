using System;
using System.Globalization;

using Assimalign.Cohesion.IdentityHub.Internal;

namespace Assimalign.Cohesion.IdentityHub;

/// <summary>
/// A group: a named set of the directory's principals, which other identity objects grant
/// access to as one.
/// </summary>
/// <remarks>
/// <para>
/// <b>A group is the validated, immutable snapshot of an
/// <see cref="IdentityGroupDescriptor"/>.</b> <see cref="Create(IdentityGroupDescriptor)"/> is
/// the one place a group is checked as a whole, and the only way one comes to exist. The same
/// check runs when a stored group is read back, so the limits may grow in a later version and
/// never shrink.
/// </para>
/// <para>
/// <b>A group does not list its members.</b> Who belongs to a group is a fact of its own, kept
/// apart from the group, so a group defined in code can still gain a member at run time and a
/// group with very many members stays one small record. Until memberships exist, a group is a
/// name other objects can refer to.
/// </para>
/// <para>
/// Changing a group is storing a replacement through an <see cref="IIdentityGroupRegistry"/>,
/// which is also what says whether it may change at all. A group written in code is handed to
/// an <see cref="IdentityHubDirectory"/>, which serves it <see cref="IsLocked"/> and refuses
/// every write to it. One created at run time is stored through the same directory.
/// </para>
/// <para>
/// There is no interface for a group. It is a value with one set of rules, not a behavior with
/// variants: a second implementation could only weaken what
/// <see cref="Create(IdentityGroupDescriptor)"/> guarantees.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// IdentityGroup admins = IdentityGroup.Create(new()
/// {
///     Name = "orders-admins",
///     DisplayName = "Orders administrators",
/// });
/// </code>
/// </example>
// Deviates from the repo interface-first rule per design decision: an identity object is a
// validated value with one fixed behavior, so it ships as a concrete type with no interface,
// following the owner's decision for app registrations (2026-11-20).
public sealed class IdentityGroup
{
    /// <summary>
    /// The longest display name, in characters.
    /// </summary>
    public const int MaxDisplayNameLength = IdentityDirectoryGrammar.MaxDisplayNameLength;

    /// <summary>
    /// The longest description, in characters.
    /// </summary>
    public const int MaxDescriptionLength = IdentityDirectoryGrammar.MaxDescriptionLength;

    private IdentityGroup(IdentityGroupId id, IdentityGroupName name, string displayName, string? description)
    {
        Id = id;
        Name = name;
        DisplayName = displayName;
        Description = description;
    }

    private IdentityGroup(IdentityGroup source, bool isLocked, IdentityHubRecordVersion? version)
        : this(source.Id, source.Name, source.DisplayName, source.Description)
    {
        IsLocked = isLocked;
        Version = version;
    }

    /// <summary>
    /// Gets the group's identifier: what a token's group claim carries. It never changes.
    /// </summary>
    public IdentityGroupId Id { get; }

    /// <summary>
    /// Gets the group's name. No other group of the directory holds it.
    /// </summary>
    public IdentityGroupName Name { get; }

    /// <summary>
    /// Gets the name a person is shown for the group. It is the text of <see cref="Name"/> when
    /// the group did not say.
    /// </summary>
    public string DisplayName { get; }

    /// <summary>
    /// Gets what the group is for, in words a person is shown, when it is described.
    /// </summary>
    public string? Description { get; }

    /// <summary>
    /// Gets a value indicating whether the group is defined in code. A locked group is read like
    /// any other and changes only where it is defined: its registry answers every write to it
    /// with <see cref="IdentityObjectWriteStatus.Locked"/>.
    /// </summary>
    /// <remarks>
    /// The directory that serves a group sets this value. It is <see langword="false"/> on a
    /// group no directory has served yet.
    /// </remarks>
    public bool IsLocked { get; }

    /// <summary>
    /// Gets the version of the stored state the group was read from, which a replace or a remove
    /// names to act only on that state. It is <see langword="null"/> for a locked group and for
    /// one no directory has served yet.
    /// </summary>
    public IdentityHubRecordVersion? Version { get; }

    /// <summary>
    /// Creates a group from a descriptor, checking it as a whole.
    /// </summary>
    /// <remarks>
    /// <para>The group is refused when:</para>
    /// <list type="bullet">
    /// <item><description>its name is missing;</description></item>
    /// <item><description>its display name is blank, holds a control character, or is longer than its limit;</description></item>
    /// <item><description>its description is blank, holds a control character other than a line break or a tab, or is longer than its limit.</description></item>
    /// </list>
    /// <para>
    /// An identifier left <see langword="default"/> is derived from the name. Whether the name
    /// is free is not decided here: the registry that stores or serves the group decides it.
    /// </para>
    /// </remarks>
    /// <param name="descriptor">The contents of the group. It is copied.</param>
    /// <returns>The group, not locked and with no version.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="descriptor"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The group breaks one of the rules above. The message names the group and the rule.</exception>
    public static IdentityGroup Create(IdentityGroupDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        IdentityGroupName name = descriptor.Name;
        if (name.Value is null)
        {
            throw new ArgumentException(ErrorMessages.GroupNameRequired, nameof(descriptor));
        }

        IdentityGroupId id = descriptor.Id.Value == Guid.Empty ? IdentityGroupId.FromName(name) : descriptor.Id;
        string label = Describe(id, name);

        string displayName = descriptor.DisplayName is { Length: > 0 } ? descriptor.DisplayName : name.Value;
        if (!IdentityClientGrammar.IsText(displayName, MaxDisplayNameLength, allowLineBreaks: false))
        {
            throw Refuse(ErrorMessages.GroupDisplayNameInvalid, label);
        }

        string? description = descriptor.Description is { Length: > 0 } ? descriptor.Description : null;
        if (description is not null &&
            !IdentityClientGrammar.IsText(description, MaxDescriptionLength, allowLineBreaks: true))
        {
            throw Refuse(ErrorMessages.GroupDescriptionInvalid, label);
        }

        return new IdentityGroup(id, name, displayName, description);
    }

    /// <summary>
    /// Returns what a message calls a group: its name, then its identifier.
    /// </summary>
    internal static string Describe(IdentityGroup group)
    {
        return Describe(group.Id, group.Name);
    }

    /// <summary>
    /// Returns the group as a directory serves one defined in code.
    /// </summary>
    internal IdentityGroup AsLocked()
    {
        return new IdentityGroup(this, isLocked: true, version: null);
    }

    /// <summary>
    /// Returns the group as a directory serves one read from its store.
    /// </summary>
    internal IdentityGroup AsStored(IdentityHubRecordVersion? version)
    {
        return new IdentityGroup(this, isLocked: false, version);
    }

    private static string Describe(IdentityGroupId id, IdentityGroupName name)
    {
        return string.Concat(name.Value, " (", id.ToString(), ")");
    }

    private static ArgumentException Refuse(string message, string label)
    {
        return new ArgumentException(string.Format(CultureInfo.CurrentCulture, message, label), "descriptor");
    }
}
