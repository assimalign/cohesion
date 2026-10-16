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
}
