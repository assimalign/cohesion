using System;
using System.Globalization;

namespace Assimalign.Cohesion.IdentityHub.Internal;

/// <summary>
/// Stores users in the <c>users</c> record set: the text of the user's identifier is the
/// record's key, the user's name in lowercase is the unique <c>name</c> index entry, and the
/// rest is a JSON payload laid out by <see cref="IdentityUserPayload"/>.
/// </summary>
/// <remarks>
/// A record is decoded by rebuilding a descriptor and creating the user from it, so a stored
/// user passes every check a new one does. The name is stored as it was written and indexed in
/// lowercase, so a user reads back with the spelling it was given.
/// </remarks>
internal sealed class IdentityUserCodec : IdentityObjectCodec<IdentityUser>
{
    private const int layout = 1;

    private IdentityUserCodec()
    {
    }

    public static IdentityUserCodec Instance { get; } = new();

    public override IdentityHubRecordSetName Set { get; } = new("users");

    internal override string RequiredMessage => ErrorMessages.DirectoryUserRequired;

    internal override string DuplicateMessage => ErrorMessages.DirectoryUserDuplicate;

    internal override string DuplicateNameMessage => ErrorMessages.DirectoryUserNameDuplicate;

    public override IdentityHubRecord Encode(IdentityUser value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var payload = new IdentityUserPayload
        {
            Layout = layout,
            Name = value.Name.Value,
            DisplayName = value.DisplayName,
            GivenName = value.GivenName,
            FamilyName = value.FamilyName,
            Email = value.Email,
            IsEmailVerified = value.IsEmailVerified,
            IsEnabled = value.IsEnabled,
        };

        return Write(
            value.Id.Value,
            NameOf(value),
            payload,
            IdentityDirectoryPayloadContext.Default.IdentityUserPayload);
    }

    public override IdentityUser Decode(IdentityHubRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        IdentityUserPayload payload =
            Read(record, IdentityDirectoryPayloadContext.Default.IdentityUserPayload)
            ?? throw new FormatException(ErrorMessages.UserPayloadInvalid);

        if (payload.Layout is not layout)
        {
            throw new FormatException(
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.UserLayoutUnknown, payload.Layout));
        }

        if (!IdentityDirectoryGrammar.TryReadId(record.Key.Value, out Guid id) ||
            payload.Name is null ||
            payload.DisplayName is null ||
            payload.IsEmailVerified is null ||
            payload.IsEnabled is null)
        {
            throw new FormatException(ErrorMessages.UserPayloadInvalid);
        }

        return IdentityUser.Create(new IdentityUserDescriptor
        {
            Id = new IdentityUserId(id),
            Name = new IdentityUserName(payload.Name),
            DisplayName = payload.DisplayName,
            GivenName = payload.GivenName,
            FamilyName = payload.FamilyName,
            Email = payload.Email,
            IsEmailVerified = payload.IsEmailVerified.Value,
            IsEnabled = payload.IsEnabled.Value,
        });
    }

    internal override Guid IdOf(IdentityUser value)
    {
        return value.Id.Value;
    }

    internal override string NameOf(IdentityUser value)
    {
        return IdentityDirectoryGrammar.Canonicalize(value.Name.Value);
    }

    internal override string Describe(IdentityUser value)
    {
        return IdentityUser.Describe(value);
    }

    internal override IdentityUser AsLocked(IdentityUser value)
    {
        return value.AsLocked();
    }

    internal override IdentityUser AsStored(IdentityUser value, IdentityHubRecordVersion version)
    {
        return value.AsStored(version);
    }
}
