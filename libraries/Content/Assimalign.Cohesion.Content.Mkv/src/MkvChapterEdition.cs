using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Content.Ebml;
using Assimalign.Cohesion.Content.Mkv.Internal;

namespace Assimalign.Cohesion.Content.Mkv;

/// <summary>An editable EditionEntry backed by the document's native chapter tree.</summary>
/// <remarks>Flags retain native metadata; ordered-chapter playback is not interpreted. Add at least
/// one chapter before saving. Unknown native children and omitted defaults remain untouched.</remarks>
public sealed class MkvChapterEdition
{
    /// <summary>Creates an empty edition with the schema's default flags.</summary>
    public MkvChapterEdition() : this(EbmlElement.Create(VInt.FromEncoded(0x45B9), EbmlElementType.MasterElement)) { }

    internal MkvChapterEdition(EbmlElement element)
    {
        Element = element;
        Chapters = MkvElements.Chapters(element);
    }
    internal EbmlElement Element { get; }

    /// <summary>Gets or sets EditionUID; null removes the optional field.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The supplied UID is zero.</exception>
    public ulong? Uid
    {
        get => MkvElements.Find(Element, 0x45BC)?.Value as ulong?;
        set
        {
            if (value is 0) { throw new ArgumentOutOfRangeException(nameof(value)); }
            MkvElements.Set(Element, 0x45BC, EbmlElementType.UnsignedInteger, value);
        }
    }
    /// <summary>Gets or sets EditionFlagDefault; absent means false.</summary>
    public bool IsDefault
    {
        get => MkvElements.Number(Element, 0x45DB) != 0;
        set => MkvElements.Set(Element, 0x45DB, EbmlElementType.UnsignedInteger, value ? 1UL : 0UL);
    }
    /// <summary>Gets or sets EditionFlagOrdered; absent means false.</summary>
    public bool IsOrdered
    {
        get => MkvElements.Number(Element, 0x45DD) != 0;
        set => MkvElements.Set(Element, 0x45DD, EbmlElementType.UnsignedInteger, value ? 1UL : 0UL);
    }
    /// <summary>Gets the editable ordered ChapterAtom children.</summary>
    public IList<MkvChapter> Chapters { get; }
}
