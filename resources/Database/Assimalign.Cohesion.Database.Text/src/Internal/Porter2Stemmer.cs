// In-house C# implementation of Snowball English (Porter2), following PostgreSQL
// REL_17_11 src/backend/snowball/libstemmer/stem_UTF_8_english.c (Snowball 2.2.0).
// The algorithm, suffix rules and exception tables follow that BSD-3 source.
// Copyright (c) 2001, Dr Martin Porter,
// Copyright (c) 2002, Richard Boulton. All rights reserved.
// Redistribution and use in source and binary forms, with or without modification,
// are permitted provided that the following conditions are met:
// 1. Redistributions of source code must retain the above copyright notice, this
//    list of conditions and the following disclaimer.
// 2. Redistributions in binary form must reproduce the above copyright notice,
//    this list of conditions and the following disclaimer in the documentation
//    and/or other materials provided with the distribution.
// 3. Neither the name of the copyright holder nor the names of its contributors
//    may be used to endorse or promote products derived from this software without
//    specific prior written permission.
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
// AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
// WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED.
// IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT,
// INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING,
// BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
// DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF
// LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE
// OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED
// OF THE POSSIBILITY OF SUCH DAMAGE.
using System;
using System.Text;

namespace Assimalign.Cohesion.Database.Text.Internal;

internal static class Porter2Stemmer
{
    // Longest suffix first, exactly one match per step, even if its region test fails.
    private static readonly (string Suffix, string Replacement)[] _step2 =
    [
        ("ization", "ize"), ("ational", "ate"), ("fulness", "ful"), ("ousness", "ous"),
        ("iveness", "ive"), ("tional", "tion"), ("biliti", "ble"), ("lessli", "less"),
        ("entli", "ent"), ("ation", "ate"), ("alism", "al"), ("aliti", "al"),
        ("ousli", "ous"), ("iviti", "ive"), ("fulli", "ful"), ("enci", "ence"),
        ("anci", "ance"), ("abli", "able"), ("izer", "ize"), ("ator", "ate"),
        ("alli", "al"), ("bli", "ble"), ("ogi", "og"), ("li", ""),
    ];

    private static readonly (string Suffix, string Replacement)[] _step3 =
    [
        ("ational", "ate"), ("tional", "tion"), ("alize", "al"), ("icate", "ic"),
        ("iciti", "ic"), ("ative", ""), ("ical", "ic"), ("ness", ""), ("ful", ""),
    ];

    private static readonly string[] _step4 =
    [
        "ement", "ance", "ence", "able", "ible", "ment", "ant", "ent", "ism",
        "ate", "iti", "ous", "ive", "ize", "ion", "al", "er", "ic",
    ];

    internal static string Stem(string word) => Stem(word, out _);

    // Per-call flags are test instrumentation, never shared mutable counters.
    internal static string Stem(string word, out Porter2Steps steps)
    {
        steps = Porter2Steps.None;
        string? exception = word switch
        {
            "skis" => "ski", "skies" => "sky", "dying" => "die", "lying" => "lie", "tying" => "tie",
            "idly" => "idl", "gently" => "gentl", "ugly" => "ugli", "early" => "earli",
            "only" => "onli", "singly" => "singl",
            "sky" or "news" or "howe" or "atlas" or "cosmos" or "bias" or "andes" => word,
            _ => null,
        };
        if (exception is not null)
        {
            steps = Porter2Steps.Exception1;
            return exception;
        }

        int scalars = 0;
        foreach (Rune unused in word.EnumerateRunes())
        {
            scalars++;
            if (scalars == 3)
            {
                break;
            }
        }

        if (scalars < 3)
        {
            steps = Porter2Steps.ShortWord;
            return word;
        }

        Span<char> buffer = word.Length < 256 ? stackalloc char[word.Length + 1] : new char[word.Length + 1];
        word.AsSpan().CopyTo(buffer);
        int length = word.Length;
        bool markedY = false;
        if (buffer[0] == '\'')
        {
            buffer[1..length].CopyTo(buffer);
            length--;
            steps |= Porter2Steps.Prelude;
        }

        for (int i = 0; i < length; i++)
        {
            if (buffer[i] == 'y' && (i == 0 || Vowel(buffer[i - 1])))
            {
                buffer[i] = 'Y';
                markedY = true;
                steps |= Porter2Steps.Prelude;
            }
        }

        ReadOnlySpan<char> text = buffer[..length];
        int r1 = text.StartsWith("gener", StringComparison.Ordinal) || text.StartsWith("arsen", StringComparison.Ordinal)
            ? 5 : text.StartsWith("commun", StringComparison.Ordinal) ? 6 : Region(text, 0);
        int r2 = Region(text, r1);
        if (r1 < length)
        {
            steps |= Porter2Steps.Regions;
        }

        // Step 0: possessives. The public tokenizer splits apostrophes; the stemmer
        // itself implements this step so the PostgreSQL dictionary remains the oracle.
        string? suffix = Match(buffer[..length], ["'s'", "'s", "'"]);
        if (suffix is not null)
        {
            length -= suffix.Length;
            steps |= Porter2Steps.Step0;
        }

        suffix = Match(buffer[..length], ["sses", "ied", "ies", "us", "ss", "s"]);
        if (suffix == "sses")
        {
            length -= 2;
            steps |= Porter2Steps.Step1A;
        }
        else if (suffix is "ied" or "ies")
        {
            int start = length - 3;
            bool twoScalars = ScalarCount(buffer[..start]) >= 2;
            Replace(buffer, ref length, 3, twoScalars ? "i" : "ie");
            steps |= Porter2Steps.Step1A;
        }
        else if (suffix == "s" && HasVowel(buffer[..Math.Max(0, length - 2)]))
        {
            length--;
            steps |= Porter2Steps.Step1A;
        }

        if (buffer[..length] is "inning" or "outing" or "canning" or "herring" or "earring" or "proceed" or "exceed" or "succeed")
        {
            steps |= Porter2Steps.Exception2;
        }
        else
        {
            suffix = Match(buffer[..length], ["eedly", "ingly", "edly", "eed", "ing", "ed"]);
            if (suffix is "eedly" or "eed")
            {
                if (length - suffix.Length >= r1)
                {
                    Replace(buffer, ref length, suffix.Length, "ee");
                    steps |= Porter2Steps.Step1B;
                }
            }
            else if (suffix is not null && HasVowel(buffer[..(length - suffix.Length)]))
            {
                length -= suffix.Length;
                steps |= Porter2Steps.Step1B;
                text = buffer[..length];
                if (text.EndsWith("at", StringComparison.Ordinal) || text.EndsWith("bl", StringComparison.Ordinal) || text.EndsWith("iz", StringComparison.Ordinal))
                {
                    buffer[length++] = 'e';
                }
                else if (length >= 2 && buffer[length - 1] == buffer[length - 2] && "bdfgmnprt".Contains(buffer[length - 1]))
                {
                    length--;
                }
                else if (length == r1 && ShortSyllable(buffer[..length]))
                {
                    buffer[length++] = 'e';
                }
            }

            if (length >= 2 && buffer[length - 1] is 'y' or 'Y'
                && !Vowel(buffer[length - 2]) && ScalarCount(buffer[..(length - 1)]) > 1)
            {
                buffer[length - 1] = 'i';
                steps |= Porter2Steps.Step1C;
            }

            foreach (var rule in _step2)
            {
                if (!buffer[..length].EndsWith(rule.Suffix, StringComparison.Ordinal))
                {
                    continue;
                }

                int start = length - rule.Suffix.Length;
                if (start >= r1
                    && (rule.Suffix != "ogi" || start > 0 && buffer[start - 1] == 'l')
                    && (rule.Suffix != "li" || start > 0 && "cdeghkmnrt".Contains(buffer[start - 1])))
                {
                    Replace(buffer, ref length, rule.Suffix.Length, rule.Replacement);
                    steps |= Porter2Steps.Step2;
                }

                break;
            }

            foreach (var rule in _step3)
            {
                if (!buffer[..length].EndsWith(rule.Suffix, StringComparison.Ordinal))
                {
                    continue;
                }

                int start = length - rule.Suffix.Length;
                if (start >= (rule.Suffix == "ative" ? r2 : r1))
                {
                    Replace(buffer, ref length, rule.Suffix.Length, rule.Replacement);
                    steps |= Porter2Steps.Step3;
                }

                break;
            }

            suffix = Match(buffer[..length], _step4);
            if (suffix is not null)
            {
                int start = length - suffix.Length;
                if (start >= r2 && (suffix != "ion" || start > 0 && buffer[start - 1] is 's' or 't'))
                {
                    length = start;
                    steps |= Porter2Steps.Step4;
                }
            }

            if (length > 0 && buffer[length - 1] == 'e'
                && (length - 1 >= r2 || length - 1 >= r1 && !ShortSyllable(buffer[..(length - 1)])))
            {
                length--;
                steps |= Porter2Steps.Step5;
            }
            else if (length >= 2 && length - 1 >= r2 && buffer[length - 1] == 'l' && buffer[length - 2] == 'l')
            {
                length--;
                steps |= Porter2Steps.Step5;
            }
        }

        if (markedY)
        {
            for (int i = 0; i < length; i++)
            {
                if (buffer[i] == 'Y')
                {
                    buffer[i] = 'y';
                }
            }
        }

        return buffer[..length].SequenceEqual(word) ? word : new string(buffer[..length]);
    }

    private static bool Vowel(int scalar) => scalar is 'a' or 'e' or 'i' or 'o' or 'u' or 'y';

    private static bool HasVowel(ReadOnlySpan<char> text)
    {
        foreach (char c in text)
        {
            if (Vowel(c))
            {
                return true;
            }
        }

        return false;
    }

    private static int ScalarCount(ReadOnlySpan<char> text)
    {
        int count = 0;
        foreach (Rune unused in text.EnumerateRunes())
        {
            count++;
        }

        return count;
    }

    private static int Region(ReadOnlySpan<char> text, int start)
    {
        bool vowel = false;
        for (int i = start; i < text.Length; i++)
        {
            int scalar = text[i];
            if (char.IsHighSurrogate(text[i]))
            {
                scalar = char.ConvertToUtf32(text[i], text[++i]);
            }

            bool next = Vowel(scalar);
            if (vowel && !next)
            {
                return i + 1;
            }

            vowel = next;
        }

        return text.Length;
    }

    private static bool ShortSyllable(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty)
        {
            return false;
        }

        Rune.DecodeLastFromUtf16(text, out Rune last, out int n);
        text = text[..^n];
        if (text.IsEmpty || Vowel(last.Value))
        {
            return false;
        }

        Rune.DecodeLastFromUtf16(text, out Rune middle, out n);
        text = text[..^n];
        if (!Vowel(middle.Value))
        {
            return false;
        }

        if (text.IsEmpty)
        {
            return true;
        }

        Rune.DecodeLastFromUtf16(text, out Rune first, out _);
        return !Vowel(first.Value) && last.Value is not ('w' or 'x' or 'Y');
    }

    private static string? Match(ReadOnlySpan<char> text, ReadOnlySpan<string> suffixes)
    {
        foreach (string suffix in suffixes)
        {
            if (text.EndsWith(suffix, StringComparison.Ordinal))
            {
                return suffix;
            }
        }

        return null;
    }

    private static void Replace(Span<char> text, ref int length, int suffixLength, string replacement)
    {
        length -= suffixLength;
        replacement.AsSpan().CopyTo(text[length..]);
        length += replacement.Length;
    }
}
