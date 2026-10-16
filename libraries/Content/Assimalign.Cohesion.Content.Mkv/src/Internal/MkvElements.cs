using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Content.Ebml;

namespace Assimalign.Cohesion.Content.Mkv.Internal;

internal static class MkvElements
{
    internal static EbmlElement? FindBody(EbmlDocument document, ulong id)
    {
        foreach (var element in document.Body.Elements) { if (element.Identifier.EncodedValue == id) { return element; } }
        return null;
    }
    internal static EbmlElement? Find(EbmlElement? parent, ulong id)
    {
        if (parent?.Type != EbmlElementType.MasterElement) { return null; }
        foreach (var child in parent.Children) { if (child.Identifier.EncodedValue == id) { return child; } }
        return null;
    }
    internal static IEnumerable<EbmlElement> All(EbmlElement? parent, ulong id)
    {
        if (parent?.Type != EbmlElementType.MasterElement) { yield break; }
        foreach (var child in parent.Children) { if (child.Identifier.EncodedValue == id) { yield return child; } }
    }
    internal static ulong Number(EbmlElement? parent, ulong id, ulong fallback = 0) => Find(parent, id)?.Value is ulong number ? number : fallback;
    internal static string? Text(EbmlElement? parent, ulong id) => Find(parent, id)?.Value as string;
    internal static void Set(EbmlElement parent, ulong id, EbmlElementType type, object? value)
    {
        var element = Find(parent, id);
        if (value is null)
        {
            if (element is not null) { parent.Children.Remove(element); }
        }
        else if (element is null) { parent.Children.Add(EbmlElement.Create(VInt.FromEncoded(id), type, value)); }
        else if (!Equals(element.Value, value)) { element.Value = value; }
    }
    internal static IList<string> Strings(EbmlElement parent, ulong id) => new MkvChildList<string>(_ => parent, id,
        element => (string)element.Value!, value => EbmlElement.Create(VInt.FromEncoded(id), EbmlElementType.AsciiString, value),
        compareValue: true);
    internal static IList<MkvChapter> Chapters(EbmlElement parent) => new MkvChildList<MkvChapter>(_ => parent, 0xB6,
        element => new MkvChapter(element), chapter => chapter.Element);
}
