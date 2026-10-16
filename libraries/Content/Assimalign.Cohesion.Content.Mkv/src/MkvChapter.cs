using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Content.Ebml;
using Assimalign.Cohesion.Content.Mkv.Internal;

namespace Assimalign.Cohesion.Content.Mkv;

/// <summary>An editable recursive ChapterAtom with exact unsigned nanosecond timestamps.</summary>
/// <remarks>Properties and collections update the native tree directly. Save validates the edited
/// schema and timestamp range. Unknown native children and original unmodified encodings are retained.</remarks>
public sealed class MkvChapter
{
    internal MkvChapter(EbmlElement element)
    {
        Element = element;
        Chapters = MkvElements.Chapters(element);
        Displays = new MkvChildList<MkvChapterDisplay>(_ => element, 0x80,
            child => new MkvChapterDisplay(child), display => display.Element);
    }

    /// <summary>Creates a chapter with the required UID and start timestamp.</summary>
    /// <param name="uid">The nonzero ChapterUID.</param>
    /// <param name="timeStart">The start in nanoseconds, independent of TimestampScale.</param>
    /// <returns>A detached editable chapter that can be added to an edition or parent chapter.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The UID is zero.</exception>
    public static MkvChapter Create(ulong uid, ulong timeStart)
    {
        ArgumentOutOfRangeException.ThrowIfZero(uid);
        var chapter = new MkvChapter(EbmlElement.Create(VInt.FromEncoded(0xB6), EbmlElementType.MasterElement));
        chapter.Uid = uid;
        chapter.TimeStart = timeStart;
        return chapter;
    }

    internal EbmlElement Element { get; }

    /// <summary>Gets or sets the nonzero ChapterUID.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The UID is zero.</exception>
    public ulong Uid
    {
        get => MkvElements.Number(Element, 0x73C4);
        set
        {
            ArgumentOutOfRangeException.ThrowIfZero(value);
            MkvElements.Set(Element, 0x73C4, EbmlElementType.UnsignedInteger, value);
        }
    }
    /// <summary>Gets or sets ChapterStringUID; null removes the optional field.</summary>
    public string? StringUid
    {
        get => MkvElements.Text(Element, 0x5654);
        set => MkvElements.Set(Element, 0x5654, EbmlElementType.Utf8String, value);
    }
    /// <summary>Gets or sets ChapterTimeStart in nanoseconds, independent of TimestampScale.</summary>
    public ulong TimeStart
    {
        get => MkvElements.Number(Element, 0x91);
        set => MkvElements.Set(Element, 0x91, EbmlElementType.UnsignedInteger, value);
    }
    /// <summary>Gets or sets the exclusive ChapterTimeEnd in nanoseconds; null removes it.</summary>
    public ulong? TimeEnd
    {
        get => MkvElements.Find(Element, 0x92)?.Value as ulong?;
        set => MkvElements.Set(Element, 0x92, EbmlElementType.UnsignedInteger, value);
    }
    /// <summary>Gets or sets ChapterFlagHidden; absent means false.</summary>
    public bool IsHidden
    {
        get => MkvElements.Number(Element, 0x98) != 0;
        set => MkvElements.Set(Element, 0x98, EbmlElementType.UnsignedInteger, value ? 1UL : 0UL);
    }
    /// <summary>Gets or sets ChapterFlagEnabled; absent means true.</summary>
    public bool IsEnabled
    {
        get => MkvElements.Number(Element, 0x4598, 1) != 0;
        set => MkvElements.Set(Element, 0x4598, EbmlElementType.UnsignedInteger, value ? 1UL : 0UL);
    }
    /// <summary>Gets the editable recursive ChapterAtom children in wire order.</summary>
    public IList<MkvChapter> Chapters { get; }
    /// <summary>Gets the editable ChapterDisplay children in wire order.</summary>
    public IList<MkvChapterDisplay> Displays { get; }
}
