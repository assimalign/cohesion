using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff;
using Assimalign.Cohesion.Content.Heif.Internal;

namespace Assimalign.Cohesion.Content.Heif;

/// <summary>The iref full-box container, with 16-bit IDs in version 0 and 32-bit IDs in version 1.</summary>
// Deviates from the repo interface-first rule per the Stage 5 composite-box brief.
public sealed class ItemReferenceBox : BmffBoxComposite
{
    /// <summary>Creates an empty ItemReferenceBox model.</summary>
    public ItemReferenceBox() { }
    /// <inheritdoc/>
    public override BmffBoxType BoxType => (BmffBoxType)0x69726566;
    internal HeifReaderOptions Options { get; set; } = new();
    /// <summary>Gets or sets the full-box version.</summary>
    public byte Version { get; set; }
    /// <summary>Gets or sets the full-box flags; zero is supported.</summary>
    public uint Flags { get; set; }

    /// <inheritdoc/>
    public override void Read(BmffStream stream)
    {
        var prefix = HeifFields.Read(stream, 4);
        Version = (byte)(prefix >> 24);
        Flags = prefix & 0xffffff;
        HeifFields.Version(Version, Flags, 1, 0, stream.Offset);
        Children.Clear();
        using var reader = stream.ReadChildren();
        var total = 0;
        while (reader.Read())
        {
            if (Children.Count >= Options.MaxExtentCount)
            {
                throw new ContentFormatException(ErrorMessages.LimitExceeded, ContentFormatErrorCode.LimitExceeded, reader.Header.Offset);
            }

            var box = reader.Current;
            var fields = GetFields(box);
            if (fields is not null)
            {
                fields.LargeIds = Version == 1;
                fields.Decode(Options);
                if (fields.ToItemIds.Length > Options.MaxExtentCount - total)
                {
                    throw new ContentFormatException(ErrorMessages.LimitExceeded, ContentFormatErrorCode.LimitExceeded, box.Offset);
                }

                total += fields.ToItemIds.Length;
            }
            Children.Add(box);
        }
    }
    /// <inheritdoc/>
    public override void Write(BmffStream stream)
    {
        SetWidths();
        HeifFields.Write(stream, ((uint)Version << 24) | Flags, 4);
        base.Write(stream);
    }
    /// <inheritdoc/>
    public override long GetPayloadSize() { SetWidths(); return 4 + base.GetPayloadSize(); }
    private void SetWidths()
    {
        HeifFields.Version(Version, Flags, 1, 0, Offset);
        foreach (var child in Children)
        {
            var fields = GetFields(child);
            if (fields is not null) { fields.Decode(Options); fields.LargeIds = Version == 1; }
        }
    }
    internal static HeifReferenceFields? GetFields(BmffBox box) => box switch
    {
        ThumbnailReferenceBox value => value.Fields,
        AuxiliaryReferenceBox value => value.Fields,
        DerivedImageReferenceBox value => value.Fields,
        ContentDescriptionReferenceBox value => value.Fields,
        BaseReferenceBox value => value.Fields,
        _ => null
    };
}
