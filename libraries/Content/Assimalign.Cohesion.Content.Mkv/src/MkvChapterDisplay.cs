using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Content.Ebml;
using Assimalign.Cohesion.Content.Mkv.Internal;

namespace Assimalign.Cohesion.Content.Mkv;

/// <summary>An editable ChapterDisplay string and its ordered language and country fields.</summary>
/// <remarks>Language and country strings retain their wire spelling. BCP 47 fields take precedence
/// over legacy language and country fields when choosing a display; this model preserves all of them.</remarks>
public sealed class MkvChapterDisplay
{
    internal MkvChapterDisplay(EbmlElement element)
    {
        Element = element;
        Languages = MkvElements.Strings(element, 0x437C);
        LanguagesBCP47 = MkvElements.Strings(element, 0x437D);
        Countries = MkvElements.Strings(element, 0x437E);
    }

    /// <summary>Creates a display with its required ChapString.</summary>
    /// <param name="text">The Unicode display text.</param>
    /// <returns>A detached display that can be added to a chapter.</returns>
    /// <exception cref="ArgumentNullException">The text is null.</exception>
    public static MkvChapterDisplay Create(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var display = new MkvChapterDisplay(EbmlElement.Create(VInt.FromEncoded(0x80), EbmlElementType.MasterElement));
        display.Text = text;
        return display;
    }

    internal EbmlElement Element { get; }

    /// <summary>Gets or sets ChapString.</summary>
    /// <exception cref="ArgumentNullException">The text is null.</exception>
    public string Text
    {
        get => MkvElements.Text(Element, 0x85) ?? string.Empty;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            MkvElements.Set(Element, 0x85, EbmlElementType.Utf8String, value);
        }
    }
    /// <summary>Gets editable explicit ChapLanguage occurrences. An empty list means the implicit "eng" default.</summary>
    public IList<string> Languages { get; }
    /// <summary>Gets editable ChapLanguageBCP47 occurrences in wire order.</summary>
    public IList<string> LanguagesBCP47 { get; }
    /// <summary>Gets editable ChapCountry occurrences in wire order.</summary>
    public IList<string> Countries { get; }
}
