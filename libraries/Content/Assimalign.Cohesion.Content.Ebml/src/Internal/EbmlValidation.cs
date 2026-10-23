using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Content.Ebml.Internal;

[global::System.Diagnostics.DebuggerDisplay("EbmlValidation")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class EbmlValidation
{
    private readonly bool _lenient;
    private readonly int _maxDiagnostics;
    private readonly List<ContentFormatException> _diagnostics = new();
    internal EbmlValidation(EbmlSchema schema, bool lenient, int maxDiagnostics = 1024)
    {
        Schema = schema;
        _lenient = lenient;
        _maxDiagnostics = maxDiagnostics;
        Diagnostics = _diagnostics.AsReadOnly();
    }
    internal EbmlSchema Schema { get; set; }
    internal IReadOnlyList<ContentFormatException> Diagnostics { get; }

    internal void Report(string message, ContentFormatErrorCode code, long position)
    {
        var error = new EbmlDataFormatException(message, code, position);
        if (!_lenient)
        {
            throw error;
        }

        if (_diagnostics.Count >= _maxDiagnostics)
        {
            throw new EbmlDataFormatException(ErrorMessages.ElementLimit, ContentFormatErrorCode.LimitExceeded, position);
        }

        _diagnostics.Add(error);
    }

    internal void Header(EbmlElementHeader header, ulong? parent, Dictionary<ulong, long> counts, long childIndex)
    {
        var definition = Schema.Find(header.Identifier);
        if (definition is null && !header.Identifier.IsValidIdentifier)
        {
            // Invalid framing remains fatal in lenient mode as well.
            throw new EbmlDataFormatException(ErrorMessages.InvalidVInt, ContentFormatErrorCode.InvalidStructure, header.Offset);
        }
        if (definition is null)
        {
            return;
        }

        if (!definition.AllowsParent(parent))
        {
            Report(ErrorMessages.SchemaViolation, ContentFormatErrorCode.InvalidStructure, header.Offset);
        }

        counts.TryGetValue(header.Identifier.EncodedValue, out var count);
        counts[header.Identifier.EncodedValue] = count + 1;
        if (definition.MaxOccurs is int maximum && count >= maximum)
        {
            Report(ErrorMessages.SchemaViolation, ContentFormatErrorCode.InvalidStructure, header.Offset);
        }

        if (header.Identifier.EncodedValue == 0xBF && childIndex != 0)
        {
            Report(ErrorMessages.SchemaViolation, ContentFormatErrorCode.InvalidStructure, header.Offset);
        }

        if (header.IsUnknownSize && !definition.UnknownSizeAllowed)
        {
            Report(ErrorMessages.UnknownSize, ContentFormatErrorCode.InvalidStructure, header.Offset);
        }

        if (header.DataSize is long length && (!EbmlValueCodec.ValidLength(definition.Type, length) ||
            length < definition.MinimumLength || length > definition.MaximumLength))
        {
            Report(ErrorMessages.InvalidSize, ContentFormatErrorCode.InvalidValue, header.DataOffset);
        }
    }

    internal void Complete(ulong? parent, Dictionary<ulong, long> counts, long position)
    {
        foreach (var definition in Schema.Definitions.Values)
        {
            if (!definition.AllowsParent(parent))
            {
                continue;
            }

            counts.TryGetValue(definition.Identifier.EncodedValue, out var count);
            if (definition.DefaultValue is not null && count == 0)
            {
                count = 1;
            }

            if (count < definition.MinOccurs)
            {
                Report(ErrorMessages.MissingElement, ContentFormatErrorCode.InvalidStructure, position);
            }
        }
    }

    internal void Value(EbmlElementHeader header, object value)
    {
        var definition = Schema.Find(header.Identifier);
        if (definition is null && !header.Identifier.IsValidIdentifier)
        {
            // Invalid framing remains fatal in lenient mode as well.
            throw new EbmlDataFormatException(ErrorMessages.InvalidVInt, ContentFormatErrorCode.InvalidStructure, header.Offset);
        }
        if (definition is not null && !definition.Contains(value))
        {
            Report(ErrorMessages.SchemaViolation, ContentFormatErrorCode.InvalidValue, header.DataOffset);
        }

        if (header.Identifier.EncodedValue is 0x4282 or 0x4283 && value is string text && text.Length == 0)
        {
            Report(ErrorMessages.InvalidValue, ContentFormatErrorCode.InvalidValue, header.DataOffset);
        }
    }

    internal void HeaderValues(EbmlHeader header)
    {
        if (header.EBMLReadVersion > header.EBMLVersion || header.DocTypeReadVersion > header.DocTypeVersion)
        {
            Report(ErrorMessages.InvalidValue, ContentFormatErrorCode.InvalidValue, header.Element.Offset);
        }

        if (header.EBMLReadVersion > 1 || header.EBMLMaxIDLength > 8 || header.EBMLMaxSizeLength > 8)
        {
            Report(ErrorMessages.UnsupportedVersion, ContentFormatErrorCode.Unsupported, header.Element.Offset);
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var extension in header.DocTypeExtension)
        {
            foreach (var child in extension.Children)
            {
                if (child.Identifier.EncodedValue == 0x4283 && child.Value is string name && !names.Add(name))
                {
                    Report(ErrorMessages.SchemaViolation, ContentFormatErrorCode.InvalidValue, child.Offset);
                }
            }
        }
    }

    private sealed class DebugView
    {
        public DebugView(EbmlValidation value)
        {
        }
    }
}
