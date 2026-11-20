using System;
using System.Buffers;
using System.Globalization;
using System.Text;

namespace Assimalign.Cohesion.JsonSchema.Internal;

/// <summary>
/// IDNA2008 label validation (RFC 5891 §5.4: the RFC 5892 derived properties and contextual rules, and
/// the RFC 5893 Bidi rule), and the UTS #46 style mapping that <c>idn-hostname</c> applies first.
/// </summary>
/// <remarks>
/// <para>
/// Derived properties follow the RFC 5892 §3 order over the runtime's general categories and the
/// <see cref="IdnaCharacterData"/> tables. <c>Unstable</c> (<c>toNFKC(toCaseFold(toNFKC(cp))) != cp</c>)
/// is computed as: not NFKC, or lowercase differs, or full case folding differs where lowercase does
/// not (a table); the Cherokee capitals fold to themselves, so lowercase does not decide them.
/// </para>
/// <para>
/// The mapping derives what the UTS #46 mapping table records for each code point (its NFKC case
/// folding, with the table's ignored code points and nontransitional deviations) instead of carrying
/// the table: a code point the table disallows fails validation after mapping instead. It agrees with
/// the Unicode 18.0 table for every code point the table does not disallow.
/// </para>
/// </remarks>
internal static class Idna
{
    /// <summary>Determines whether a label satisfies RFC 5891 §5.4 as a U-label, apart from NFC.</summary>
    /// <remarks>
    /// It checks the hyphen restrictions (§4.2.3.1), the leading combining mark (§4.2.3.2), and each
    /// code point's derived property with the contextual rules of RFC 5892 Appendix A (§4.2.3.3).
    /// </remarks>
    public static bool IsValidULabel(ReadOnlySpan<int> label)
    {
        if (label.IsEmpty || label[0] == '-' || label[^1] == '-' || (label.Length >= 4 && label[2] == '-' && label[3] == '-'))
        {
            return false;
        }

        if (CharUnicodeInfo.GetUnicodeCategory(label[0]) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
        {
            return false;
        }

        for (int index = 0; index < label.Length; index++)
        {
            bool valid = GetDerivedProperty(label[index]) switch
            {
                DerivedProperty.Valid => true,
                DerivedProperty.ContextJ => IsContextJSatisfied(label, index),
                DerivedProperty.ContextO => IsContextOSatisfied(label, index),
                _ => false,
            };

            if (!valid)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Determines whether a label holds a character of Bidi_Class R, AL or AN (RFC 5893 §1.4).</summary>
    public static bool IsRightToLeftLabel(ReadOnlySpan<int> label)
    {
        foreach (int codePoint in label)
        {
            if (IdnaCharacterData.GetBidiClass(codePoint) is IdnaBidiClass.RightToLeft or IdnaBidiClass.ArabicLetter or IdnaBidiClass.ArabicNumber)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Determines whether a valid U-label satisfies the six conditions of the RFC 5893 §2 Bidi rule.</summary>
    public static bool SatisfiesBidiRule(ReadOnlySpan<int> label)
    {
        // 1. The first character is L, R or AL, and decides the label's direction.
        IdnaBidiClass first = IdnaCharacterData.GetBidiClass(label[0]);
        bool rightToLeft = first is IdnaBidiClass.RightToLeft or IdnaBidiClass.ArabicLetter;
        if (!rightToLeft && first != IdnaBidiClass.LeftToRight)
        {
            return false;
        }

        bool europeanNumber = false;
        bool arabicNumber = false;
        IdnaBidiClass last = first;
        foreach (int codePoint in label)
        {
            IdnaBidiClass bidiClass = IdnaCharacterData.GetBidiClass(codePoint);

            // 2. A right-to-left label holds no L; 5. a left-to-right label holds no R, AL or AN.
            if (rightToLeft
                ? bidiClass == IdnaBidiClass.LeftToRight
                : bidiClass is IdnaBidiClass.RightToLeft or IdnaBidiClass.ArabicLetter or IdnaBidiClass.ArabicNumber)
            {
                return false;
            }

            europeanNumber |= bidiClass == IdnaBidiClass.EuropeanNumber;
            arabicNumber |= bidiClass == IdnaBidiClass.ArabicNumber;
            if (bidiClass != IdnaBidiClass.NonspacingMark)
            {
                last = bidiClass;
            }
        }

        // 3. and 4. A right-to-left label ends in R, AL, EN or AN before any NSM, and mixes no EN with AN;
        // 6. a left-to-right label ends in L or EN before any NSM.
        return rightToLeft
            ? last is IdnaBidiClass.RightToLeft or IdnaBidiClass.ArabicLetter or IdnaBidiClass.EuropeanNumber or IdnaBidiClass.ArabicNumber
                && !(europeanNumber && arabicNumber)
            : last is IdnaBidiClass.LeftToRight or IdnaBidiClass.EuropeanNumber;
    }

    /// <summary>
    /// Maps a name as UTS #46 nontransitional processing does before breaking it into labels: the four
    /// full stops become <c>.</c>, ignored code points are removed, the deviations (<c>ß</c>, <c>ς</c>
    /// and the joiners) stay, every other code point becomes its NFKC case folding, and the result is
    /// normalized to NFC.
    /// </summary>
    /// <returns>
    /// The mapped name, or <see langword="null"/> when the value holds a lone surrogate or an unassigned
    /// code point, or a mapping that holds a full stop. UTS #46 disallows all three.
    /// </returns>
    public static string? Map(ReadOnlySpan<char> value)
    {
        StringBuilder builder = new(value.Length);
        int separators = 0;
        for (int position = 0; position < value.Length;)
        {
            // Rejecting unassigned code points first also keeps U+FFFE, which the runtime's normalization
            // refuses with an exception, away from it.
            if (Rune.DecodeFromUtf16(value.Slice(position), out Rune rune, out int consumed) != OperationStatus.Done
                || Rune.GetUnicodeCategory(rune) == UnicodeCategory.OtherNotAssigned)
            {
                return null;
            }

            position += consumed;
            int codePoint = rune.Value;
            if (codePoint is '.' or 0x3002 or 0xFF0E or 0xFF61)
            {
                builder.Append('.');
                separators++;
            }
            else if (codePoint is 0x00DF or 0x03C2 or 0x200C or 0x200D)
            {
                builder.Append((char)codePoint);
            }
            else if (codePoint is >= 0xFF01 and <= 0xFF5E)
            {
                // Fullwidth ASCII, which NFKC maps too; mapped here as well so that a runtime without
                // normalization data still reads it.
                AppendMapping(builder, new Rune(codePoint - 0xFEE0));
            }
            else if (!IsIgnored(codePoint))
            {
                AppendMapping(builder, rune);
            }
        }

        string mapped = builder.ToString();
        if (!mapped.IsNormalized(NormalizationForm.FormC))
        {
            mapped = mapped.Normalize(NormalizationForm.FormC);
        }

        return mapped.AsSpan().Count('.') == separators ? mapped : null;
    }

    /// <summary>
    /// Appends a code point's NFKC case folding: its case folding, normalized to NFKC and case folded
    /// again when the folding is not in NFKC.
    /// </summary>
    private static void AppendMapping(StringBuilder builder, Rune rune)
    {
        Span<char> folded = stackalloc char[3];
        int length = Fold(rune, folded);
        if (rune.Value < 0x80 || folded.Slice(0, length).IsNormalized(NormalizationForm.FormKC))
        {
            builder.Append(folded.Slice(0, length));
            return;
        }

        // A compatibility character, whose normalization can hold capitals, and final sigma (U+03F2 is
        // one), which folds to sigma: only a final sigma written as such stays one.
        foreach (Rune normalized in folded.Slice(0, length).ToString().Normalize(NormalizationForm.FormKC).EnumerateRunes())
        {
            if (normalized.Value == 0x03C2)
            {
                builder.Append((char)0x03C3);
            }
            else
            {
                builder.Append(folded.Slice(0, Fold(normalized, folded)));
            }
        }
    }

    /// <summary>Gets a code point's IDNA2008 derived property (RFC 5892 §3).</summary>
    private static DerivedProperty GetDerivedProperty(int codePoint)
    {
        // Exceptions (§2.6).
        switch (codePoint)
        {
            case 0x00DF or 0x03C2 or 0x06FD or 0x06FE or 0x0F0B or 0x3007:
                return DerivedProperty.Valid;
            case 0x00B7 or 0x0375 or 0x05F3 or 0x05F4 or 0x30FB or (>= 0x0660 and <= 0x0669) or (>= 0x06F0 and <= 0x06F9):
                return DerivedProperty.ContextO;
            case 0x0640 or 0x07FA or 0x302E or 0x302F or (>= 0x3031 and <= 0x3035) or 0x303B:
                return DerivedProperty.Disallowed;
        }

        UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(codePoint);
        if (category == UnicodeCategory.OtherNotAssigned)
        {
            // Unassigned (§2.10), or a noncharacter, which IgnorableProperties disallows.
            return DerivedProperty.Disallowed;
        }

        if (codePoint is '-' or (>= '0' and <= '9') or (>= 'a' and <= 'z'))
        {
            // LDH (§2.7).
            return DerivedProperty.Valid;
        }

        if (codePoint is 0x200C or 0x200D)
        {
            // JoinControl (§2.8).
            return DerivedProperty.ContextJ;
        }

        if (IsUnstable(codePoint)
            || IdnaCharacterData.IsDefaultIgnorable(codePoint)
            || codePoint is (>= 0x20D0 and <= 0x20FF) or (>= 0x1D100 and <= 0x1D24F)
            || codePoint is (>= 0x1100 and <= 0x11FF) or (>= 0xA960 and <= 0xA97F) or (>= 0xD7B0 and <= 0xD7FF))
        {
            // Unstable (§2.2), IgnorableProperties (§2.4), IgnorableBlocks (§2.5) and OldHangulJamo (§2.9).
            return DerivedProperty.Disallowed;
        }

        // LetterDigits (§2.1).
        return category is UnicodeCategory.LowercaseLetter or UnicodeCategory.UppercaseLetter or UnicodeCategory.OtherLetter
            or UnicodeCategory.DecimalDigitNumber or UnicodeCategory.ModifierLetter or UnicodeCategory.NonSpacingMark
            or UnicodeCategory.SpacingCombiningMark
            ? DerivedProperty.Valid
            : DerivedProperty.Disallowed;
    }

    /// <summary>Determines whether <c>toNFKC(toCaseFold(toNFKC(cp)))</c> differs from a code point (RFC 5892 §2.2).</summary>
    private static bool IsUnstable(int codePoint)
    {
        if (codePoint is >= 0x13A0 and <= 0x13F5)
        {
            return false;
        }

        Rune rune = new(codePoint);
        if (IdnaCharacterData.IsCaseFoldUnstable(codePoint) || Rune.ToLowerInvariant(rune) != rune)
        {
            return true;
        }

        Span<char> buffer = stackalloc char[2];
        return !buffer.Slice(0, rune.EncodeToUtf16(buffer)).IsNormalized(NormalizationForm.FormKC);
    }

    /// <summary>The ZERO WIDTH NON-JOINER and ZERO WIDTH JOINER rules (RFC 5892 A.1 and A.2).</summary>
    private static bool IsContextJSatisfied(ReadOnlySpan<int> label, int index)
    {
        if (index > 0 && IdnaCharacterData.IsVirama(label[index - 1]))
        {
            return true;
        }

        if (label[index] != 0x200C)
        {
            return false;
        }

        // (Joining_Type:{L,D})(Joining_Type:T)*U+200C(Joining_Type:T)*(Joining_Type:{R,D})
        int before = index - 1;
        while (before >= 0 && IdnaCharacterData.GetJoiningType(label[before]) == IdnaJoiningType.Transparent)
        {
            before--;
        }

        int after = index + 1;
        while (after < label.Length && IdnaCharacterData.GetJoiningType(label[after]) == IdnaJoiningType.Transparent)
        {
            after++;
        }

        return before >= 0
            && IdnaCharacterData.GetJoiningType(label[before]) is IdnaJoiningType.LeftJoining or IdnaJoiningType.DualJoining
            && after < label.Length
            && IdnaCharacterData.GetJoiningType(label[after]) is IdnaJoiningType.RightJoining or IdnaJoiningType.DualJoining;
    }

    /// <summary>The CONTEXTO rules (RFC 5892 A.3 to A.9).</summary>
    private static bool IsContextOSatisfied(ReadOnlySpan<int> label, int index)
    {
        switch (label[index])
        {
            case 0x00B7:
                // MIDDLE DOT, between two 'l's.
                return index > 0 && label[index - 1] == 'l' && index + 1 < label.Length && label[index + 1] == 'l';
            case 0x0375:
                // GREEK LOWER NUMERAL SIGN (KERAIA), before a Greek character.
                return index + 1 < label.Length && IdnaCharacterData.IsGreek(label[index + 1]);
            case 0x05F3 or 0x05F4:
                // HEBREW PUNCTUATION GERESH and GERSHAYIM, after a Hebrew character.
                return index > 0 && IdnaCharacterData.IsHebrew(label[index - 1]);
            case 0x30FB:
                // KATAKANA MIDDLE DOT, in a label with a Hiragana, Katakana or Han character.
                foreach (int codePoint in label)
                {
                    if (IdnaCharacterData.IsHiraganaKatakanaOrHan(codePoint))
                    {
                        return true;
                    }
                }

                return false;
            case >= 0x0660 and <= 0x0669:
                // ARABIC-INDIC DIGITS, in a label without EXTENDED ARABIC-INDIC DIGITS.
                return !label.ContainsAnyInRange(0x06F0, 0x06F9);
            default:
                // EXTENDED ARABIC-INDIC DIGITS, in a label without ARABIC-INDIC DIGITS.
                return !label.ContainsAnyInRange(0x0660, 0x0669);
        }
    }

    /// <summary>Determines whether UTS #46 maps a code point to nothing.</summary>
    private static bool IsIgnored(int codePoint) =>
        codePoint is 0x00AD or 0x034F or 0x115F or 0x1160 or 0x17B4 or 0x17B5 or (>= 0x180B and <= 0x180F) or 0x200B
            or (>= 0x2060 and <= 0x2064) or (>= 0x206A and <= 0x206F) or 0x3164 or (>= 0xFE00 and <= 0xFE0F) or 0xFEFF
            or 0xFFA0 or (>= 0x1BCA0 and <= 0x1BCA3) or (>= 0x1D173 and <= 0x1D17A) or (>= 0xE0100 and <= 0xE01EF);

    /// <summary>
    /// Writes a code point's full case folding (CaseFolding.txt statuses C and F): the runtime's
    /// lowercase mapping, except where folding differs from it.
    /// </summary>
    /// <returns>The number of UTF-16 code units written.</returns>
    private static int Fold(Rune rune, Span<char> destination)
    {
        if (rune.Value == 0x0130)
        {
            // The runtime leaves U+0130 unmapped; it folds to i and a combining dot above.
            destination[0] = 'i';
            destination[1] = (char)0x0307;
            return 2;
        }

        Rune lower = Rune.ToLowerInvariant(rune);
        int value = lower.Value;
        char folded = value switch
        {
            // Cherokee folds to the capitals, which lowercase to these.
            >= 0x13F8 and <= 0x13FD => (char)(value - 0x0008),
            >= 0xAB70 and <= 0xABBF => (char)(value - 0x97D0),
            0x0345 => (char)0x03B9,
            0x1C80 => (char)0x0432,
            0x1C81 => (char)0x0434,
            0x1C82 => (char)0x043E,
            0x1C83 => (char)0x0441,
            0x1C84 or 0x1C85 => (char)0x0442,
            0x1C86 => (char)0x044A,
            0x1C87 => (char)0x0463,
            0x1C88 => (char)0xA64B,
            _ => '\0',
        };

        if (folded != '\0')
        {
            destination[0] = folded;
            return 1;
        }

        // A Greek vowel with a ypogegrammeni folds to the vowel, any perispomeni, and iota.
        char vowel = value switch
        {
            >= 0x1F80 and <= 0x1F87 => (char)(value - 0x0080),
            >= 0x1F90 and <= 0x1F97 => (char)(value - 0x0070),
            >= 0x1FA0 and <= 0x1FA7 => (char)(value - 0x0040),
            0x1FB2 => (char)0x1F70,
            0x1FB3 or 0x1FB7 => (char)0x03B1,
            0x1FB4 => (char)0x03AC,
            0x1FC2 => (char)0x1F74,
            0x1FC3 or 0x1FC7 => (char)0x03B7,
            0x1FC4 => (char)0x03AE,
            0x1FF2 => (char)0x1F7C,
            0x1FF3 or 0x1FF7 => (char)0x03C9,
            0x1FF4 => (char)0x03CE,
            _ => '\0',
        };

        if (vowel == '\0')
        {
            return lower.EncodeToUtf16(destination);
        }

        int length = 0;
        destination[length++] = vowel;
        if (value is 0x1FB7 or 0x1FC7 or 0x1FF7)
        {
            destination[length++] = (char)0x0342;
        }

        destination[length++] = (char)0x03B9;
        return length;
    }

    private enum DerivedProperty
    {
        Valid,
        ContextJ,
        ContextO,
        Disallowed,
    }
}
