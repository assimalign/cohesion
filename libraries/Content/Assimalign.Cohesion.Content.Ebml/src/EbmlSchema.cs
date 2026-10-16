using System.Collections.Frozen;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Content.Ebml;

/// <summary>An immutable schema shared safely by independent readers.</summary>
public sealed class EbmlSchema
{
    private readonly FrozenDictionary<ulong, EbmlElementDefinition> _definitions;
    internal EbmlSchema(Dictionary<ulong, EbmlElementDefinition> definitions)
    {
        _definitions = definitions.ToFrozenDictionary();
    }

    /// <summary>Gets the built-in RFC 8794 header and global element definitions.</summary>
    public static EbmlSchema Header { get; } = CreateHeader();
    /// <summary>Gets the immutable element definitions.</summary>
    public IReadOnlyDictionary<ulong, EbmlElementDefinition> Definitions => _definitions;

    /// <summary>Finds a definition by encoded ID.</summary>
    /// <param name="identifier">The element ID.</param>
    /// <returns>The definition, or null for an unknown ID.</returns>
    public EbmlElementDefinition? Find(VInt identifier) =>
        _definitions.TryGetValue(identifier.EncodedValue, out var definition) ? definition : null;

    private static EbmlSchema CreateHeader()
    {
        var builder = new EbmlSchemaBuilder();
        var header = VInt.FromEncoded(0x1A45DFA3);
        builder.Add(EbmlElementDefinition.Create(header, "EBML", EbmlElementType.MasterElement));
        foreach (var (id, name) in new (ulong, string)[] {
            (0x4286, "EBMLVersion"), (0x42F7, "EBMLReadVersion"), (0x42F2, "EBMLMaxIDLength"),
            (0x42F3, "EBMLMaxSizeLength"), (0x4287, "DocTypeVersion"), (0x4285, "DocTypeReadVersion") })
        {
            builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(id), name, EbmlElementType.UnsignedInteger, [header]));
        }

        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x4282), "DocType", EbmlElementType.AsciiString, [header]));
        var extension = VInt.FromEncoded(0x4281);
        builder.Add(EbmlElementDefinition.Create(extension, "DocTypeExtension", EbmlElementType.MasterElement, [header]));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x4283), "DocTypeExtensionName", EbmlElementType.AsciiString, [extension]));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x4284), "DocTypeExtensionVersion", EbmlElementType.UnsignedInteger, [extension]));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0xEC), "Void", EbmlElementType.Binary, global: true));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0xBF), "CRC-32", EbmlElementType.Binary, global: true));
        return builder.Build();
    }
}
