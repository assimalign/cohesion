using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff;
using Assimalign.Cohesion.Content.Heif.Internal;

namespace Assimalign.Cohesion.Content.Heif;

/// <summary>The ipco ordered property container; unknown children are preserved.</summary>
// Deviates from the repo interface-first rule per the Stage 5 composite-box brief.
public sealed class ItemPropertyContainerBox : BmffBoxComposite
{
    /// <summary>Creates an empty ItemPropertyContainerBox model.</summary>
    public ItemPropertyContainerBox() { }
    /// <inheritdoc/>
    public override long Offset => Header.Offset;
    /// <inheritdoc/>
    public override long Limit => Header.TotalSize ?? 0;
    /// <inheritdoc/>
    public override BmffBoxType BoxType => (BmffBoxType)0x6970636f;
    internal HeifReaderOptions Options { get; set; } = new();

    /// <inheritdoc/>
    public override void Read(BmffStream stream)
    {
        Children.Clear();
        using var reader = stream.ReadChildren();
        while (reader.Read())
        {
            if (Children.Count >= Options.MaxPropertyCount)
            {
                throw new ContentFormatException(ErrorMessages.LimitExceeded, ContentFormatErrorCode.LimitExceeded, reader.Header.Offset);
            }

            Children.Add(reader.Current);
        }
    }
}
