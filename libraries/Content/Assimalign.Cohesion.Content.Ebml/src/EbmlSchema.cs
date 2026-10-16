using System;
using System.Collections.Frozen;
using System.Collections.Generic;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Ebml.Internal;

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

    /// <summary>Validates a document tree against this schema before writing.</summary>
    /// <param name="document">The document to validate.</param>
    /// <param name="lenient">Whether violations are returned instead of thrown.</param>
    /// <param name="maxDepth">The maximum master depth (one through 256).</param>
    /// <returns>Recorded diagnostics; empty for a valid document.</returns>
    /// <exception cref="ArgumentNullException">The document is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The depth is invalid.</exception>
    /// <exception cref="ContentFormatException">A violation occurs in strict mode, or a safety limit is exceeded.</exception>
    public IReadOnlyList<ContentFormatException> Validate(EbmlDocument document, bool lenient = false, int maxDepth = 64)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDepth, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxDepth, 256);
        var validation = new EbmlValidation(this, lenient);
        var counts = new Dictionary<ulong, long>();
        ValidateElement(document.Header.Element, null, counts, 0, 0, maxDepth, validation, 4, 8);
        long index = 1;
        foreach (var element in document.Body.Elements)
        {
            ValidateElement(element, null, counts, index++, 0, maxDepth, validation,
                (int)Math.Min(8UL, document.Header.EBMLMaxIDLength), (int)Math.Min(8UL, document.Header.EBMLMaxSizeLength));
        }

        validation.Complete(null, counts, document.Header.Element.Offset);
        validation.HeaderValues(document.Header);
        ValidateBody(document, validation);
        return validation.Diagnostics;
    }

    internal static void ValidateBody(EbmlDocument document, EbmlValidation validation)
    {
        var roots = 0;
        foreach (var element in document.Body.Elements)
        {
            var definition = validation.Schema.Find(element.Identifier);
            if (definition?.IsGlobal == true)
            {
                continue;
            }

            roots++;
            if (definition is not null && element.Type != EbmlElementType.MasterElement)
            {
                validation.Report(ErrorMessages.BodyRequired, ContentFormatErrorCode.InvalidStructure, element.Offset);
            }
        }
        if (roots != 1)
        {
            validation.Report(ErrorMessages.BodyRequired, ContentFormatErrorCode.InvalidStructure, document.Header.Element.Offset);
        }
    }

    private long ValidateElement(EbmlElement element, ulong? parent, Dictionary<ulong, long> counts, long index,
        int depth, int maxDepth, EbmlValidation validation, int maxIdLength, int maxSizeLength)
    {
        ArgumentNullException.ThrowIfNull(element);
        long length;
        if (element.Type == EbmlElementType.MasterElement)
        {
            if (depth >= maxDepth)
            {
                throw new EbmlDataFormatException(ErrorMessages.DepthLimit, ContentFormatErrorCode.LimitExceeded, element.Offset);
            }

            var children = new Dictionary<ulong, long>();
            long childIndex = 0;
            length = 0;
            foreach (var child in element.Children)
            {
                var size = ValidateElement(child, element.Identifier.EncodedValue, children, childIndex++, depth + 1, maxDepth, validation,
                    maxIdLength, maxSizeLength);
                if (size > 0x00FFFFFFFFFFFFFEL - length)
                {
                    throw new EbmlDataFormatException(ErrorMessages.InvalidSize, ContentFormatErrorCode.LimitExceeded, element.Offset);
                }

                length += size;
            }
            validation.Complete(element.Identifier.EncodedValue, children, element.Offset);
        }
        else
        {
            length = element.RawPayload?.Length ?? (element.Value is BinarySegment binary ? binary.Length :
                EbmlValueCodec.GetWireLength(Find(element.Identifier), element.Type, element.Value!));
        }

        var width = element.IsUnknownSize ? (element.SizeLength == 0 ? 8 : element.SizeLength)
            : Math.Max(element.SizeLength, VInt.EncodeSize((ulong)length).Length);
        var header = new EbmlElementHeader(element.Identifier,
            element.IsUnknownSize ? VInt.UnknownSize(width) : VInt.EncodeSize((ulong)length, width), element.Offset, element.Type);
        if (element.Identifier.Length > maxIdLength || width > maxSizeLength)
        {
            validation.Report(ErrorMessages.LengthLimit, ContentFormatErrorCode.LimitExceeded, element.Offset);
        }

        validation.Header(header, parent, counts, index);
        var definition = Find(element.Identifier);
        if (definition is not null && definition.Type != element.Type)
        {
            validation.Report(ErrorMessages.InvalidType, ContentFormatErrorCode.InvalidValue, element.Offset);
        }
        else if (element.Type != EbmlElementType.MasterElement)
        {
            validation.Value(header, element.Value!);
        }

        return element.Identifier.Length + width + length;
    }

    private static EbmlSchema CreateHeader()
    {
        var builder = new EbmlSchemaBuilder();
        var header = VInt.FromEncoded(0x1A45DFA3);
        builder.Add(EbmlElementDefinition.Create(header, "EBML", EbmlElementType.MasterElement, minOccurs: 1, maxOccurs: 1));
        foreach (var (id, name) in new (ulong, string)[] {
            (0x4286, "EBMLVersion"), (0x42F7, "EBMLReadVersion"), (0x42F2, "EBMLMaxIDLength"),
            (0x42F3, "EBMLMaxSizeLength"), (0x4287, "DocTypeVersion"), (0x4285, "DocTypeReadVersion") })
        {
            var fallback = id == 0x42F2 ? 4UL : id == 0x42F3 ? 8UL : 1UL;
            builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(id), name, EbmlElementType.UnsignedInteger, [header],
                minOccurs: 1, maxOccurs: 1, minimumValue: id == 0x42F2 ? 4UL : 1UL, defaultValue: fallback));
        }

        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x4282), "DocType", EbmlElementType.AsciiString, [header],
            minOccurs: 1, maxOccurs: 1, minimumLength: 1));
        var extension = VInt.FromEncoded(0x4281);
        builder.Add(EbmlElementDefinition.Create(extension, "DocTypeExtension", EbmlElementType.MasterElement, [header]));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x4283), "DocTypeExtensionName", EbmlElementType.AsciiString, [extension],
            minOccurs: 1, maxOccurs: 1, minimumLength: 1));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x4284), "DocTypeExtensionVersion", EbmlElementType.UnsignedInteger, [extension],
            minOccurs: 1, maxOccurs: 1, minimumValue: 1UL));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0xEC), "Void", EbmlElementType.Binary, global: true));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0xBF), "CRC-32", EbmlElementType.Binary,
            global: true, maxOccurs: 1, minimumLength: 4, maximumLength: 4));
        return builder.Build();
    }
}
