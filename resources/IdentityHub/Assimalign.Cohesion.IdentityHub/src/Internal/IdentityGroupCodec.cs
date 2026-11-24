using System;
using System.Globalization;

namespace Assimalign.Cohesion.IdentityHub.Internal;

/// <summary>
/// Stores groups in the <c>groups</c> record set: the text of the group's identifier is the
/// record's key, the group's name in lowercase is the unique <c>name</c> index entry, and the
/// rest is a JSON payload laid out by <see cref="IdentityGroupPayload"/>.
/// </summary>
/// <remarks>
/// A record is decoded by rebuilding a descriptor and creating the group from it, so a stored
/// group passes every check a new one does. The name is stored as it was written and indexed in
/// lowercase, so a group reads back with the spelling it was given.
/// </remarks>
internal sealed class IdentityGroupCodec : IdentityObjectCodec<IdentityGroup>
{
    private const int layout = 1;

    private IdentityGroupCodec()
    {
    }

    public static IdentityGroupCodec Instance { get; } = new();

    public override IdentityHubRecordSetName Set { get; } = new("groups");

    internal override string RequiredMessage => ErrorMessages.DirectoryGroupRequired;

    internal override string DuplicateMessage => ErrorMessages.DirectoryGroupDuplicate;

    internal override string DuplicateNameMessage => ErrorMessages.DirectoryGroupNameDuplicate;

    public override IdentityHubRecord Encode(IdentityGroup value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var payload = new IdentityGroupPayload
        {
            Layout = layout,
            Name = value.Name.Value,
            DisplayName = value.DisplayName,
            Description = value.Description,
        };

        return Write(
            value.Id.Value,
            NameOf(value),
            payload,
            IdentityDirectoryPayloadContext.Default.IdentityGroupPayload);
    }

    public override IdentityGroup Decode(IdentityHubRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        IdentityGroupPayload payload =
            Read(record, IdentityDirectoryPayloadContext.Default.IdentityGroupPayload)
            ?? throw new FormatException(ErrorMessages.GroupPayloadInvalid);

        if (payload.Layout is not layout)
        {
            throw new FormatException(
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.GroupLayoutUnknown, payload.Layout));
        }

        if (!IdentityDirectoryGrammar.TryReadId(record.Key.Value, out Guid id) ||
            payload.Name is null ||
            payload.DisplayName is null)
        {
            throw new FormatException(ErrorMessages.GroupPayloadInvalid);
        }

        return IdentityGroup.Create(new IdentityGroupDescriptor
        {
            Id = new IdentityGroupId(id),
            Name = new IdentityGroupName(payload.Name),
            DisplayName = payload.DisplayName,
            Description = payload.Description,
        });
    }

    internal override Guid IdOf(IdentityGroup value)
    {
        return value.Id.Value;
    }

    internal override string NameOf(IdentityGroup value)
    {
        return IdentityDirectoryGrammar.Canonicalize(value.Name.Value);
    }

    internal override string Describe(IdentityGroup value)
    {
        return IdentityGroup.Describe(value);
    }

    internal override IdentityGroup AsLocked(IdentityGroup value)
    {
        return value.AsLocked();
    }

    internal override IdentityGroup AsStored(IdentityGroup value, IdentityHubRecordVersion version)
    {
        return value.AsStored(version);
    }
}
