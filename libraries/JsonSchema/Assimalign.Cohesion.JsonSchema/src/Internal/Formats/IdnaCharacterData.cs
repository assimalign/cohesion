using System;
using System.Globalization;

namespace Assimalign.Cohesion.JsonSchema.Internal;

/// <summary>
/// The Unicode character properties IDNA2008 needs beyond what the BCL exposes: Bidi_Class (RFC 5893),
/// Joining_Type and Canonical_Combining_Class (the RFC 5892 Appendix A contextual rules), Script (the
/// same rules), and the code points whose case folding differs from their lowercase mapping (the
/// RFC 5892 <c>Unstable</c> category).
/// </summary>
/// <remarks>
/// <para>
/// The tables are generated from the Unicode Character Database: Bidi_Class, Canonical_Combining_Class
/// and case folding from Unicode 15.1, Joining_Type and Script from Unicode 15.0. They hold only what a
/// valid label can contain (code points of the letter, digit and mark categories and the IDNA2008
/// exceptions); any other code point is DISALLOWED before a table is read, so ranges merge across
/// them. Properties the general category implies are derived from the runtime's category instead of
/// stored: a combining mark is NSM in Bidi terms and Transparent in joining terms unless a table says
/// otherwise. Unassigned code points in the right-to-left blocks carry the default R or AL that
/// DerivedBidiClass.txt gives them, so letters a newer runtime knows read correctly.
/// </para>
/// <para>
/// Each table is a sorted list of inclusive <c>[first, last]</c> code point pairs, compiled into the
/// assembly's data section.
/// </para>
/// </remarks>
internal static class IdnaCharacterData
{
    /// <summary>Gets a code point's Bidi_Class, as far as RFC 5893 distinguishes them.</summary>
    public static IdnaBidiClass GetBidiClass(int codePoint)
    {
        if (Contains(BidiL, codePoint))
        {
            return IdnaBidiClass.LeftToRight;
        }

        UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(codePoint);
        if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark)
        {
            return IdnaBidiClass.NonspacingMark;
        }

        if (Contains(BidiR, codePoint))
        {
            return IdnaBidiClass.RightToLeft;
        }

        if (Contains(BidiAl, codePoint))
        {
            return IdnaBidiClass.ArabicLetter;
        }

        if (Contains(BidiAn, codePoint))
        {
            return IdnaBidiClass.ArabicNumber;
        }

        if (Contains(BidiEn, codePoint))
        {
            return IdnaBidiClass.EuropeanNumber;
        }

        if (Contains(BidiEs, codePoint))
        {
            return IdnaBidiClass.EuropeanSeparator;
        }

        if (Contains(BidiOn, codePoint))
        {
            return IdnaBidiClass.OtherNeutral;
        }

        if (Contains(BidiBn, codePoint))
        {
            return IdnaBidiClass.BoundaryNeutral;
        }

        return IdnaBidiClass.LeftToRight;
    }

    /// <summary>Gets a code point's Joining_Type.</summary>
    public static IdnaJoiningType GetJoiningType(int codePoint)
    {
        if (Contains(JoiningD, codePoint))
        {
            return IdnaJoiningType.DualJoining;
        }

        if (Contains(JoiningR, codePoint))
        {
            return IdnaJoiningType.RightJoining;
        }

        if (Contains(JoiningL, codePoint))
        {
            return IdnaJoiningType.LeftJoining;
        }

        if (Contains(JoiningC, codePoint))
        {
            return IdnaJoiningType.JoinCausing;
        }

        if (Contains(JoiningT, codePoint))
        {
            return IdnaJoiningType.Transparent;
        }

        if (Contains(JoiningU, codePoint))
        {
            return IdnaJoiningType.NonJoining;
        }

        UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(codePoint);
        return category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format
            ? IdnaJoiningType.Transparent
            : IdnaJoiningType.NonJoining;
    }

    /// <summary>Determines whether a code point's Canonical_Combining_Class is Virama (9).</summary>
    public static bool IsVirama(int codePoint) => Contains(Virama, codePoint);

    /// <summary>Determines whether a code point's Script is Greek.</summary>
    public static bool IsGreek(int codePoint) => Contains(ScriptGreek, codePoint);

    /// <summary>Determines whether a code point's Script is Hebrew.</summary>
    public static bool IsHebrew(int codePoint) => Contains(ScriptHebrew, codePoint);

    /// <summary>Determines whether a code point's Script is Hiragana, Katakana or Han.</summary>
    public static bool IsHiraganaKatakanaOrHan(int codePoint) =>
        Contains(ScriptHiragana, codePoint) || Contains(ScriptKatakana, codePoint) || Contains(ScriptHan, codePoint);

    /// <summary>
    /// Determines whether a code point's full case folding differs although the runtime's invariant
    /// lowercase mapping leaves it unchanged, which makes it unstable under RFC 5892 although it is
    /// lowercase and in NFKC as far as the runtime can tell.
    /// </summary>
    public static bool IsCaseFoldUnstable(int codePoint) => Contains(CaseFoldUnstable, codePoint);

    /// <summary>Determines whether a code point is Default_Ignorable_Code_Point (RFC 5892 IgnorableProperties).</summary>
    public static bool IsDefaultIgnorable(int codePoint) => Contains(DefaultIgnorable, codePoint);

    private static bool Contains(ReadOnlySpan<int> ranges, int codePoint)
    {
        int low = 0;
        int high = (ranges.Length / 2) - 1;
        while (low <= high)
        {
            int middle = (low + high) >>> 1;
            if (codePoint < ranges[middle * 2])
            {
                high = middle - 1;
            }
            else if (codePoint > ranges[(middle * 2) + 1])
            {
                low = middle + 1;
            }
            else
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Default_Ignorable_Code_Point (DerivedCoreProperties.txt).</summary>
    private static ReadOnlySpan<int> DefaultIgnorable =>
    [
        0x00AD, 0x00AD, 0x034F, 0x034F, 0x061C, 0x061C, 0x115F, 0x1160, 0x17B4, 0x17B5, 0x180B, 0x180F,
        0x200B, 0x200F, 0x202A, 0x202E, 0x2060, 0x206F, 0x3164, 0x3164, 0xFE00, 0xFE0F, 0xFEFF, 0xFEFF,
        0xFFA0, 0xFFA0, 0xFFF0, 0xFFF8, 0x1BCA0, 0x1BCA3, 0x1D173, 0x1D17A, 0xE0000, 0xE0FFF,
    ];

    /// <summary>Marks whose Bidi_Class is L rather than the NSM their category implies.</summary>
    private static ReadOnlySpan<int> BidiL =>
    [
        0x0CBF, 0x0CC6, 0x11A07, 0x11A08, 0x11C3F, 0x11C3F,
    ];

    /// <summary>Bidi_Class R, with the unassigned code points of right-to-left blocks at their default.</summary>
    private static ReadOnlySpan<int> BidiR =>
    [
        0x0590, 0x0590, 0x05C8, 0x05FF, 0x07C0, 0x07EA, 0x07F4, 0x07FC, 0x0800, 0x0815, 0x081A, 0x081A,
        0x0824, 0x0824, 0x0828, 0x0828, 0x082E, 0x0858, 0x085C, 0x085F, 0xFB1D, 0xFB1D, 0xFB1F, 0xFB4F,
        0x10800, 0x10A00, 0x10A04, 0x10A04, 0x10A07, 0x10A0B, 0x10A10, 0x10A37, 0x10A3B, 0x10A3E, 0x10A49, 0x10AE4,
        0x10AE7, 0x10CF9, 0x10D40, 0x10EAA, 0x10EAE, 0x10EBF, 0x10F00, 0x10F2F, 0x10F70, 0x10F81, 0x10F8A, 0x10FFF,
        0x1E800, 0x1E8C6, 0x1E8D7, 0x1E943, 0x1E94B, 0x1EC6F, 0x1ECC0, 0x1ECFF, 0x1ED50, 0x1EDFF, 0x1EF00, 0x1EFFF,
    ];

    /// <summary>Bidi_Class AL, with the unassigned code points of Arabic-letter blocks at their default.</summary>
    private static ReadOnlySpan<int> BidiAl =>
    [
        0x0620, 0x064A, 0x066E, 0x066F, 0x0671, 0x06D5, 0x06E5, 0x06E6, 0x06EE, 0x06EF, 0x06FA, 0x0710,
        0x0712, 0x072F, 0x074B, 0x07A5, 0x07B1, 0x07BF, 0x0860, 0x0897, 0x08A0, 0x08C9, 0xFB50, 0xFDFB,
        0xFE70, 0xFEFE, 0x10D00, 0x10D23, 0x10D28, 0x10D2F, 0x10D3A, 0x10D3F, 0x10EC0, 0x10EFC, 0x10F30, 0x10F45,
        0x10F5A, 0x10F6F, 0x1EC70, 0x1ECBF, 0x1ED00, 0x1ED4F, 0x1EE00, 0x1EEFF,
    ];

    /// <summary>Bidi_Class AN.</summary>
    private static ReadOnlySpan<int> BidiAn =>
    [
        0x0660, 0x0669, 0x10D30, 0x10D39,
    ];

    /// <summary>Bidi_Class EN.</summary>
    private static ReadOnlySpan<int> BidiEn =>
    [
        0x0030, 0x0039, 0x06F0, 0x06F9, 0xFF10, 0xFF19, 0x1D7CE, 0x1D7FF, 0x1FBF0, 0x1FBF9,
    ];

    /// <summary>Bidi_Class ES.</summary>
    private static ReadOnlySpan<int> BidiEs =>
    [
        0x002D, 0x002D,
    ];

    /// <summary>Bidi_Class ON.</summary>
    private static ReadOnlySpan<int> BidiOn =>
    [
        0x00B7, 0x00B7, 0x02B9, 0x02BA, 0x02C6, 0x02CF, 0x02EC, 0x02EC, 0x0374, 0x0375, 0x2E2F, 0x2E2F,
        0x30FB, 0x30FB, 0xA67F, 0xA67F, 0xA717, 0xA71F, 0xA788, 0xA788,
    ];

    /// <summary>Bidi_Class BN.</summary>
    private static ReadOnlySpan<int> BidiBn =>
    [
        0x200C, 0x200D,
    ];

    /// <summary>Joining_Type D (dual joining).</summary>
    private static ReadOnlySpan<int> JoiningD =>
    [
        0x0620, 0x0620, 0x0626, 0x0626, 0x0628, 0x0628, 0x062A, 0x062E, 0x0633, 0x063F, 0x0641, 0x0647,
        0x0649, 0x064A, 0x066E, 0x066F, 0x0678, 0x0687, 0x069A, 0x06BF, 0x06C1, 0x06C2, 0x06CC, 0x06CC,
        0x06CE, 0x06CE, 0x06D0, 0x06D1, 0x06FA, 0x06FC, 0x06FF, 0x06FF, 0x0712, 0x0714, 0x071A, 0x071D,
        0x071F, 0x0727, 0x0729, 0x0729, 0x072B, 0x072B, 0x072D, 0x072E, 0x074E, 0x0758, 0x075C, 0x076A,
        0x076D, 0x0770, 0x0772, 0x0772, 0x0775, 0x0777, 0x077A, 0x077F, 0x07CA, 0x07EA, 0x0841, 0x0845,
        0x0848, 0x0848, 0x084A, 0x0853, 0x0855, 0x0855, 0x0860, 0x0860, 0x0862, 0x0865, 0x0868, 0x0868,
        0x0886, 0x0886, 0x0889, 0x088D, 0x08A0, 0x08A9, 0x08AF, 0x08B0, 0x08B3, 0x08B8, 0x08BA, 0x08C8,
        0x1807, 0x1807, 0x1820, 0x1878, 0x1887, 0x18A8, 0x18AA, 0x18AA, 0xA840, 0xA871, 0x10AC0, 0x10AC4,
        0x10AD3, 0x10AD6, 0x10AD8, 0x10ADC, 0x10ADE, 0x10AE0, 0x10AEB, 0x10AEE, 0x10B80, 0x10B80, 0x10B82, 0x10B82,
        0x10B86, 0x10B88, 0x10B8A, 0x10B8B, 0x10B8D, 0x10B8D, 0x10B90, 0x10B90, 0x10BAD, 0x10BAE, 0x10D01, 0x10D21,
        0x10D23, 0x10D23, 0x10F30, 0x10F32, 0x10F34, 0x10F44, 0x10F51, 0x10F53, 0x10F70, 0x10F73, 0x10F76, 0x10F81,
        0x10FB0, 0x10FB0, 0x10FB2, 0x10FB3, 0x10FB8, 0x10FB8, 0x10FBB, 0x10FBC, 0x10FBE, 0x10FBF, 0x10FC1, 0x10FC1,
        0x10FC4, 0x10FC4, 0x10FCA, 0x10FCA, 0x1E900, 0x1E943,
    ];

    /// <summary>Joining_Type L (left joining).</summary>
    private static ReadOnlySpan<int> JoiningL =>
    [
        0xA872, 0xA872, 0x10ACD, 0x10ACD, 0x10AD7, 0x10AD7, 0x10D00, 0x10D00, 0x10FCB, 0x10FCB,
    ];

    /// <summary>Joining_Type R (right joining).</summary>
    private static ReadOnlySpan<int> JoiningR =>
    [
        0x0622, 0x0625, 0x0627, 0x0627, 0x0629, 0x0629, 0x062F, 0x0632, 0x0648, 0x0648, 0x0671, 0x0673,
        0x0675, 0x0677, 0x0688, 0x0699, 0x06C0, 0x06C0, 0x06C3, 0x06CB, 0x06CD, 0x06CD, 0x06CF, 0x06CF,
        0x06D2, 0x06D3, 0x06D5, 0x06D5, 0x06EE, 0x06EF, 0x0710, 0x0710, 0x0715, 0x0719, 0x071E, 0x071E,
        0x0728, 0x0728, 0x072A, 0x072A, 0x072C, 0x072C, 0x072F, 0x072F, 0x074D, 0x074D, 0x0759, 0x075B,
        0x076B, 0x076C, 0x0771, 0x0771, 0x0773, 0x0774, 0x0778, 0x0779, 0x0840, 0x0840, 0x0846, 0x0847,
        0x0849, 0x0849, 0x0854, 0x0854, 0x0856, 0x0858, 0x0867, 0x0867, 0x0869, 0x086A, 0x0870, 0x0882,
        0x088E, 0x088E, 0x08AA, 0x08AC, 0x08AE, 0x08AE, 0x08B1, 0x08B2, 0x08B9, 0x08B9, 0x10AC5, 0x10AC5,
        0x10AC7, 0x10AC7, 0x10AC9, 0x10ACA, 0x10ACE, 0x10AD2, 0x10ADD, 0x10ADD, 0x10AE1, 0x10AE1, 0x10AE4, 0x10AE4,
        0x10AEF, 0x10AEF, 0x10B81, 0x10B81, 0x10B83, 0x10B85, 0x10B89, 0x10B89, 0x10B8C, 0x10B8C, 0x10B8E, 0x10B8F,
        0x10B91, 0x10B91, 0x10BA9, 0x10BAC, 0x10D22, 0x10D22, 0x10F33, 0x10F33, 0x10F54, 0x10F54, 0x10F74, 0x10F75,
        0x10FB4, 0x10FB6, 0x10FB9, 0x10FBA, 0x10FBD, 0x10FBD, 0x10FC2, 0x10FC3, 0x10FC9, 0x10FC9,
    ];

    /// <summary>Joining_Type C (join causing).</summary>
    private static ReadOnlySpan<int> JoiningC =>
    [
        0x0640, 0x0640, 0x07FA, 0x07FA, 0x0883, 0x0885, 0x180A, 0x180A, 0x200D, 0x200D,
    ];

    /// <summary>Joining_Type T for code points outside the Mn, Me and Cf categories that imply it.</summary>
    private static ReadOnlySpan<int> JoiningT =>
    [
        0x1E94B, 0x1E94B,
    ];

    /// <summary>Joining_Type U for Mn, Me and Cf code points, which otherwise imply T.</summary>
    private static ReadOnlySpan<int> JoiningU =>
    [
        0x0600, 0x0605, 0x06DD, 0x06DD, 0x0890, 0x0891, 0x08E2, 0x08E2, 0x180E, 0x180E, 0x200C, 0x200C,
        0x2066, 0x2069, 0x110BD, 0x110BD, 0x110CD, 0x110CD,
    ];

    /// <summary>Canonical_Combining_Class 9 (Virama).</summary>
    private static ReadOnlySpan<int> Virama =>
    [
        0x094D, 0x094D, 0x09CD, 0x09CD, 0x0A4D, 0x0A4D, 0x0ACD, 0x0ACD, 0x0B4D, 0x0B4D, 0x0BCD, 0x0BCD,
        0x0C4D, 0x0C4D, 0x0CCD, 0x0CCD, 0x0D3B, 0x0D3C, 0x0D4D, 0x0D4D, 0x0DCA, 0x0DCA, 0x0E3A, 0x0E3A,
        0x0EBA, 0x0EBA, 0x0F84, 0x0F84, 0x1039, 0x103A, 0x1714, 0x1715, 0x1734, 0x1734, 0x17D2, 0x17D2,
        0x1A60, 0x1A60, 0x1B44, 0x1B44, 0x1BAA, 0x1BAB, 0x1BF2, 0x1BF3, 0x2D7F, 0x2D7F, 0xA806, 0xA806,
        0xA82C, 0xA82C, 0xA8C4, 0xA8C4, 0xA953, 0xA953, 0xA9C0, 0xA9C0, 0xAAF6, 0xAAF6, 0xABED, 0xABED,
        0x10A3F, 0x10A3F, 0x11046, 0x11046, 0x11070, 0x11070, 0x1107F, 0x1107F, 0x110B9, 0x110B9, 0x11133, 0x11134,
        0x111C0, 0x111C0, 0x11235, 0x11235, 0x112EA, 0x112EA, 0x1134D, 0x1134D, 0x11442, 0x11442, 0x114C2, 0x114C2,
        0x115BF, 0x115BF, 0x1163F, 0x1163F, 0x116B6, 0x116B6, 0x1172B, 0x1172B, 0x11839, 0x11839, 0x1193D, 0x1193E,
        0x119E0, 0x119E0, 0x11A34, 0x11A34, 0x11A47, 0x11A47, 0x11A99, 0x11A99, 0x11C3F, 0x11C3F, 0x11D44, 0x11D45,
        0x11D97, 0x11D97, 0x11F41, 0x11F42,
    ];

    /// <summary>Script=Greek, over the code points a label can hold.</summary>
    private static ReadOnlySpan<int> ScriptGreek =>
    [
        0x0370, 0x0373, 0x0375, 0x03E1, 0x03F0, 0x03FF, 0x1D26, 0x1D2A, 0x1D5D, 0x1D61, 0x1D66, 0x1D6A,
        0x1DBF, 0x1DBF, 0x1F00, 0x1FFB, 0x2126, 0x2126, 0xAB65, 0xAB65, 0x1D242, 0x1D244,
    ];

    /// <summary>Script=Hebrew, over the code points a label can hold.</summary>
    private static ReadOnlySpan<int> ScriptHebrew =>
    [
        0x0591, 0x05F4, 0xFB1D, 0xFB4F,
    ];

    /// <summary>Script=Hiragana, over the code points a label can hold.</summary>
    private static ReadOnlySpan<int> ScriptHiragana =>
    [
        0x3041, 0x3096, 0x309D, 0x309F, 0x1B001, 0x1B11F, 0x1B132, 0x1B152,
    ];

    /// <summary>Script=Katakana, over the code points a label can hold.</summary>
    private static ReadOnlySpan<int> ScriptKatakana =>
    [
        0x30A1, 0x30FA, 0x30FD, 0x30FF, 0x31F0, 0x31FF, 0xFF66, 0xFF6F, 0xFF71, 0xFF9D, 0x1AFF0, 0x1B000,
        0x1B120, 0x1B122, 0x1B155, 0x1B167,
    ];

    /// <summary>Script=Han, over the code points a label can hold.</summary>
    private static ReadOnlySpan<int> ScriptHan =>
    [
        0x3005, 0x3005, 0x3007, 0x3007, 0x3400, 0x9FFF, 0xF900, 0xFAD9, 0x16FE3, 0x16FE3, 0x16FF0, 0x16FF1,
        0x20000, 0x2EBE0, 0x2F800, 0x323AF,
    ];

    /// <summary>
    /// Code points whose full case folding differs where the runtime's invariant lowercase mapping does not:
    /// U+0130, which the runtime leaves unmapped, and code points that are lowercase and in NFKC.
    /// </summary>
    private static ReadOnlySpan<int> CaseFoldUnstable =>
    [
        0x0130, 0x0130, 0x0345, 0x0345, 0x13F8, 0x13FD, 0x1C80, 0x1C88, 0x1F80, 0x1F87, 0x1F90, 0x1F97, 0x1FA0, 0x1FA7,
        0x1FB2, 0x1FB4, 0x1FB7, 0x1FB7, 0x1FC2, 0x1FC4, 0x1FC7, 0x1FC7, 0x1FF2, 0x1FF4, 0x1FF7, 0x1FF7,
        0xAB70, 0xABBF,
    ];
}

/// <summary>The Bidi_Class values RFC 5893 distinguishes.</summary>
internal enum IdnaBidiClass
{
    /// <summary>L.</summary>
    LeftToRight,

    /// <summary>R.</summary>
    RightToLeft,

    /// <summary>AL.</summary>
    ArabicLetter,

    /// <summary>AN.</summary>
    ArabicNumber,

    /// <summary>EN.</summary>
    EuropeanNumber,

    /// <summary>ES.</summary>
    EuropeanSeparator,

    /// <summary>ON.</summary>
    OtherNeutral,

    /// <summary>BN.</summary>
    BoundaryNeutral,

    /// <summary>NSM.</summary>
    NonspacingMark,
}

/// <summary>The Joining_Type values.</summary>
internal enum IdnaJoiningType
{
    /// <summary>U.</summary>
    NonJoining,

    /// <summary>D.</summary>
    DualJoining,

    /// <summary>L.</summary>
    LeftJoining,

    /// <summary>R.</summary>
    RightJoining,

    /// <summary>C.</summary>
    JoinCausing,

    /// <summary>T.</summary>
    Transparent,
}
