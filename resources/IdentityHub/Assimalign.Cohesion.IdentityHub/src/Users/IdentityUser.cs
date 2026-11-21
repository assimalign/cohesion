using System;
using System.Globalization;

using Assimalign.Cohesion.IdentityHub.Internal;

namespace Assimalign.Cohesion.IdentityHub;

/// <summary>
/// A user: one person's account in the identity hub's directory, with the name the person signs
/// in with and what the hub says about the person in a token.
/// </summary>
/// <remarks>
/// <para>
/// <b>A user is the validated, immutable snapshot of an <see cref="IdentityUserDescriptor"/>.</b>
/// <see cref="Create(IdentityUserDescriptor)"/> is the one place a user is checked as a whole,
/// and the only way one comes to exist. The same check runs when a stored user is read back, so
/// the limits may grow in a later version and never shrink.
/// </para>
/// <para>
/// <b>A user is an account, not a credential.</b> What a user proves its identity with (a
/// password, a passkey, an identity another provider vouches for) is not part of the user: it
/// changes on its own schedule, and a read of a user never carries verifier material.
/// </para>
/// <para>
/// Changing a user is storing a replacement through an <see cref="IIdentityUserRegistry"/>,
/// which is also what says whether it may change at all. A user written in code is handed to an
/// <see cref="IdentityHubDirectory"/>, which serves it <see cref="IsLocked"/> and refuses every
/// write to it. One created at run time is stored through the same directory.
/// </para>
/// <para>
/// There is no interface for a user. It is a value with one set of rules, not a behavior with
/// variants: a second implementation could only weaken what
/// <see cref="Create(IdentityUserDescriptor)"/> guarantees.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// IdentityUser ada = IdentityUser.Create(new()
/// {
///     Name = "ada@contoso.example",
///     DisplayName = "Ada Lovelace",
///     Email = "ada@contoso.example",
///     IsEmailVerified = true,
/// });
/// </code>
/// </example>
// Deviates from the repo interface-first rule per design decision: an identity object is a
// validated value with one fixed behavior, so it ships as a concrete type with no interface,
// following the owner's decision for app registrations (2026-11-20).
public sealed class IdentityUser
{
    /// <summary>
    /// The longest display name, in characters.
    /// </summary>
    public const int MaxDisplayNameLength = IdentityDirectoryGrammar.MaxDisplayNameLength;

    /// <summary>
    /// The longest given name and the longest family name, in characters.
    /// </summary>
    public const int MaxPersonNameLength = IdentityDirectoryGrammar.MaxPersonNameLength;

    /// <summary>
    /// The longest email address, in characters.
    /// </summary>
    public const int MaxEmailLength = IdentityDirectoryGrammar.MaxEmailLength;

    private IdentityUser(
        IdentityUserId id,
        IdentityUserName name,
        string displayName,
        string? givenName,
        string? familyName,
        string? email,
        bool isEmailVerified,
        bool isEnabled)
    {
        Id = id;
        Name = name;
        DisplayName = displayName;
        GivenName = givenName;
        FamilyName = familyName;
        Email = email;
        IsEmailVerified = isEmailVerified;
        IsEnabled = isEnabled;
    }

    private IdentityUser(IdentityUser source, bool isLocked, IdentityHubRecordVersion? version)
        : this(
            source.Id,
            source.Name,
            source.DisplayName,
            source.GivenName,
            source.FamilyName,
            source.Email,
            source.IsEmailVerified,
            source.IsEnabled)
    {
        IsLocked = isLocked;
        Version = version;
    }

    /// <summary>
    /// Gets the user's identifier: what a token names the user by. It never changes.
    /// </summary>
    public IdentityUserId Id { get; }

    /// <summary>
    /// Gets the name the user signs in with. No other user of the directory holds it.
    /// </summary>
    public IdentityUserName Name { get; }

    /// <summary>
    /// Gets the name a person is shown for the user. It is the text of <see cref="Name"/> when
    /// the user did not say.
    /// </summary>
    public string DisplayName { get; }

    /// <summary>
    /// Gets the user's given name, when it is known.
    /// </summary>
    public string? GivenName { get; }

    /// <summary>
    /// Gets the user's family name, when it is known.
    /// </summary>
    public string? FamilyName { get; }

    /// <summary>
    /// Gets the user's email address, when it is known. It is contact information: a user does
    /// not sign in with it, and two users may share one.
    /// </summary>
    public string? Email { get; }

    /// <summary>
    /// Gets a value indicating whether <see cref="Email"/> is known to reach the user.
    /// </summary>
    public bool IsEmailVerified { get; }

    /// <summary>
    /// Gets a value indicating whether the user may sign in. A user that is not enabled stays in
    /// the directory, keeps its identifier and its name, and is issued nothing.
    /// </summary>
    public bool IsEnabled { get; }

    /// <summary>
    /// Gets a value indicating whether the user is defined in code. A locked user is read like
    /// any other and changes only where it is defined: its registry answers every write to it
    /// with <see cref="IdentityObjectWriteStatus.Locked"/>.
    /// </summary>
    /// <remarks>
    /// The directory that serves a user sets this value. It is <see langword="false"/> on a user
    /// no directory has served yet.
    /// </remarks>
    public bool IsLocked { get; }

    /// <summary>
    /// Gets the version of the stored state the user was read from, which a replace or a remove
    /// names to act only on that state. It is <see langword="null"/> for a locked user and for
    /// one no directory has served yet.
    /// </summary>
    public IdentityHubRecordVersion? Version { get; }

    /// <summary>
    /// Creates a user from a descriptor, checking it as a whole.
    /// </summary>
    /// <remarks>
    /// <para>The user is refused when:</para>
    /// <list type="bullet">
    /// <item><description>its name is missing;</description></item>
    /// <item><description>its display name, given name, or family name is blank, holds a control character, or is longer than its limit;</description></item>
    /// <item><description>its email address does not have the shape of one;</description></item>
    /// <item><description>its email address is marked verified and none is given.</description></item>
    /// </list>
    /// <para>
    /// An identifier left <see langword="default"/> is derived from the name. Whether the name
    /// is free is not decided here: the registry that stores or serves the user decides it.
    /// </para>
    /// </remarks>
    /// <param name="descriptor">The contents of the user. It is copied.</param>
    /// <returns>The user, not locked and with no version.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="descriptor"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The user breaks one of the rules above. The message names the user and the rule.</exception>
    public static IdentityUser Create(IdentityUserDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        IdentityUserName name = descriptor.Name;
        if (name.Value is null)
        {
            throw new ArgumentException(ErrorMessages.UserNameRequired, nameof(descriptor));
        }

        IdentityUserId id = descriptor.Id.Value == Guid.Empty ? IdentityUserId.FromName(name) : descriptor.Id;
        string label = Describe(id, name);

        string displayName = descriptor.DisplayName is { Length: > 0 } ? descriptor.DisplayName : name.Value;
        if (!IdentityClientGrammar.IsText(displayName, MaxDisplayNameLength, allowLineBreaks: false))
        {
            throw Refuse(ErrorMessages.UserDisplayNameInvalid, label);
        }

        string? givenName = descriptor.GivenName is { Length: > 0 } ? descriptor.GivenName : null;
        if (givenName is not null &&
            !IdentityClientGrammar.IsText(givenName, MaxPersonNameLength, allowLineBreaks: false))
        {
            throw Refuse(ErrorMessages.UserGivenNameInvalid, label);
        }

        string? familyName = descriptor.FamilyName is { Length: > 0 } ? descriptor.FamilyName : null;
        if (familyName is not null &&
            !IdentityClientGrammar.IsText(familyName, MaxPersonNameLength, allowLineBreaks: false))
        {
            throw Refuse(ErrorMessages.UserFamilyNameInvalid, label);
        }

        // The address is not echoed: it may hold the character that made it invalid.
        string? email = descriptor.Email is { Length: > 0 } ? descriptor.Email : null;
        if (email is not null && !IdentityDirectoryGrammar.IsEmail(email))
        {
            throw Refuse(ErrorMessages.UserEmailInvalid, label);
        }

        if (descriptor.IsEmailVerified && email is null)
        {
            throw Refuse(ErrorMessages.UserEmailRequired, label);
        }

        return new IdentityUser(
            id,
            name,
            displayName,
            givenName,
            familyName,
            email,
            descriptor.IsEmailVerified,
            descriptor.IsEnabled);
    }

    /// <summary>
    /// Returns what a message calls a user: its name, then its identifier.
    /// </summary>
    internal static string Describe(IdentityUser user)
    {
        return Describe(user.Id, user.Name);
    }

    /// <summary>
    /// Returns the user as a directory serves one defined in code.
    /// </summary>
    internal IdentityUser AsLocked()
    {
        return new IdentityUser(this, isLocked: true, version: null);
    }

    /// <summary>
    /// Returns the user as a directory serves one read from its store.
    /// </summary>
    internal IdentityUser AsStored(IdentityHubRecordVersion? version)
    {
        return new IdentityUser(this, isLocked: false, version);
    }

    private static string Describe(IdentityUserId id, IdentityUserName name)
    {
        return string.Concat(name.Value, " (", id.ToString(), ")");
    }

    private static ArgumentException Refuse(string message, string label)
    {
        return new ArgumentException(string.Format(CultureInfo.CurrentCulture, message, label), "descriptor");
    }
}
