using System.Collections.Generic;

namespace Assimalign.Cohesion.Content.Ebml;

/// <summary>Typed RFC 8794 header accessors over an editable, ordered element tree.</summary>
public sealed class EbmlHeader
{
    /// <summary>Creates a header with the generic DocType "ebml" and RFC defaults.</summary>
    public EbmlHeader()
    {
        Element = EbmlElement.Create(VInt.FromEncoded(0x1A45DFA3), EbmlElementType.MasterElement);
        DocType = "ebml";
    }
    internal EbmlHeader(EbmlElement element) { Element = element; }

    /// <summary>Gets the ordered header tree, including unknown elements and DocTypeExtension masters.</summary>
    public EbmlElement Element { get; }
    /// <summary>Gets or sets EBMLVersion, defaulting to one when absent.</summary>
    /// <exception cref="System.ArgumentException">An existing header element has a type incompatible with the assigned value.</exception>
    public ulong EBMLVersion { get => Number(0x4286, 1); set => Set(0x4286, EbmlElementType.UnsignedInteger, value); }
    /// <summary>Gets or sets EBMLReadVersion, defaulting to one when absent.</summary>
    /// <exception cref="System.ArgumentException">An existing header element has a type incompatible with the assigned value.</exception>
    public ulong EBMLReadVersion { get => Number(0x42F7, 1); set => Set(0x42F7, EbmlElementType.UnsignedInteger, value); }
    /// <summary>Gets or sets the maximum ID width, defaulting to four.</summary>
    /// <exception cref="System.ArgumentException">An existing header element has a type incompatible with the assigned value.</exception>
    public ulong EBMLMaxIDLength { get => Number(0x42F2, 4); set => Set(0x42F2, EbmlElementType.UnsignedInteger, value); }
    /// <summary>Gets or sets the maximum size width, defaulting to eight.</summary>
    /// <exception cref="System.ArgumentException">An existing header element has a type incompatible with the assigned value.</exception>
    public ulong EBMLMaxSizeLength { get => Number(0x42F3, 8); set => Set(0x42F3, EbmlElementType.UnsignedInteger, value); }
    /// <summary>Gets or sets the required ASCII document type.</summary>
    /// <exception cref="System.ArgumentException">An existing header element has a type incompatible with the assigned value.</exception>
    public string DocType { get => Find(0x4282)?.Value as string ?? string.Empty; set => Set(0x4282, EbmlElementType.AsciiString, value); }
    /// <summary>Gets or sets the DocType version, defaulting to one.</summary>
    /// <exception cref="System.ArgumentException">An existing header element has a type incompatible with the assigned value.</exception>
    public ulong DocTypeVersion { get => Number(0x4287, 1); set => Set(0x4287, EbmlElementType.UnsignedInteger, value); }
    /// <summary>Gets or sets the minimum DocType reader version, defaulting to one.</summary>
    /// <exception cref="System.ArgumentException">An existing header element has a type incompatible with the assigned value.</exception>
    public ulong DocTypeReadVersion { get => Number(0x4285, 1); set => Set(0x4285, EbmlElementType.UnsignedInteger, value); }
    /// <summary>Enumerates DocTypeExtension master elements in wire order. Edit them through Element.Children.</summary>
    public IEnumerable<EbmlElement> DocTypeExtension
    {
        get
        {
            foreach (var child in Element.Children)
            {
                if (child.Identifier.EncodedValue == 0x4281)
                {
                    yield return child;
                }
            }
        }
    }

    private ulong Number(ulong id, ulong fallback) => Find(id)?.Value is ulong number ? number : fallback;
    private EbmlElement? Find(ulong id)
    {
        foreach (var child in Element.Children)
        {
            if (child.Identifier.EncodedValue == id)
            {
                return child;
            }
        }

        return null;
    }
    private void Set(ulong id, EbmlElementType type, object value)
    {
        var existing = Find(id);
        if (existing is not null)
        {
            existing.Value = value;
        }
        else
        {
            Element.Children.Add(EbmlElement.Create(VInt.FromEncoded(id), type, value));
        }
    }
}
