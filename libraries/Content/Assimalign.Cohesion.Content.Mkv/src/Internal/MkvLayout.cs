using System;
using System.Collections.Generic;
using System.IO;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Ebml;

namespace Assimalign.Cohesion.Content.Mkv.Internal;

// Retain native identities for Matroska position repair. Generic measurement and original-byte
// copying belong to EBML; this layer selects Matroska's explicit numeric widths and tracks edits.
internal sealed class MkvLayout
{
    private readonly Dictionary<EbmlElement, Snapshot> _original = new();
    private readonly HashSet<EbmlElement> _relocatedPositions = new();
    private readonly EbmlSchema _schema;
    private readonly int _maxDepth;

    internal MkvLayout(EbmlSchema schema, int maxDepth) { _schema = schema; _maxDepth = maxDepth; }

    internal long Capture(EbmlElement element, Stream source, long origin)
    {
        long payload = 0;
        var children = element.Type == EbmlElementType.MasterElement ? new EbmlElement[element.Children.Count] : Array.Empty<EbmlElement>();
        long length;
        if (element.Type == EbmlElementType.MasterElement)
        {
            for (var i = 0; i < children.Length; i++)
            {
                children[i] = element.Children[i];
                payload = Add(payload, Capture(children[i], source, origin), element.Offset);
            }
            length = Add(payload, element.Identifier.Length + element.SizeLength, element.Offset);
        }
        else if (element.Value is BinarySegment binary)
        { length = Add(binary.Length, element.Identifier.Length + element.SizeLength, element.Offset); }
        else
        {
            length = EbmlWriter.GetEncodedLength(element, _schema, _maxDepth);
        }
        _original.Add(element, new Snapshot(element.Value, children, element.SizeLength, element.IsUnknownSize,
            BinarySegment.Create(source, element.Offset - origin, length)));
        return length;
    }

    internal bool Unchanged(EbmlElement element)
    {
        if (_relocatedPositions.Contains(element) || !_original.TryGetValue(element, out var original) || element.SizeLength != original.SizeLength ||
            element.IsUnknownSize != original.UnknownSize || !Equals(element.Value, original.Value)) { return false; }
        if (element.Type == EbmlElementType.MasterElement)
        {
            if (element.Children.Count != original.Children.Length) { return false; }
            for (var i = 0; i < original.Children.Length; i++)
            {
                if (!ReferenceEquals(element.Children[i], original.Children[i]) || !Unchanged(element.Children[i])) { return false; }
            }
        }
        return true;
    }

    internal void RemoveChangedChecksums(EbmlElement element)
    {
        if (element.Type != EbmlElementType.MasterElement || Unchanged(element)) { return; }
        for (var i = element.Children.Count - 1; i >= 0; i--)
        {
            if (element.Children[i].Identifier.EncodedValue == 0xBF) { element.Children.RemoveAt(i); }
            else { RemoveChangedChecksums(element.Children[i]); }
        }
    }

    internal long OriginalLength(EbmlElement element) => _original[element].Bytes.Length;
    internal void Relocate(EbmlElement field, ulong position)
    { _relocatedPositions.Add(field); field.Value = position; }

    internal long Measure(EbmlElement element, int depth = 0)
    {
        if (depth > _maxDepth)
        { throw new ContentFormatException(ErrorMessages.DepthLimit, ContentFormatErrorCode.LimitExceeded, element.Offset); }
        if (Unchanged(element)) { return _original[element].Bytes.Length; }
        if (element.Type == EbmlElementType.MasterElement)
        {
            var payload = PayloadLength(element, depth);
            return Add(payload, element.Identifier.Length + Width(element, payload), element.Offset);
        }
        if (element.Value is BinarySegment binary)
        {
            return EbmlWriter.GetEncodedLength(element.Identifier, element.Type, binary, _schema,
                sizeLength: Width(element, binary.Length));
        }
        var widths = ScalarWidths(element);
        return EbmlWriter.GetEncodedLength(element.Identifier, element.Type, element.Value!, _schema, widths.Value, widths.Size);
    }

    private long PayloadLength(EbmlElement element, int depth)
    {
        long length = 0;
        foreach (var child in element.Children) { length = Add(length, Measure(child, depth + 1), element.Offset); }
        return length;
    }

    internal void Write(Stream destination, EbmlElement element)
    {
        using var writer = EbmlWriter.Create(destination, _schema, maxDepth: _maxDepth);
        Write(writer, element, 0);
    }

    private void Write(EbmlWriter writer, EbmlElement element, int depth)
    {
        if (Unchanged(element)) { writer.WriteRawElement(_original[element].Bytes); return; }
        if (element.Type == EbmlElementType.MasterElement)
        {
            var payload = PayloadLength(element, depth);
            writer.BeginMaster(element.Identifier, element.IsUnknownSize ? null : payload, Width(element, payload));
            foreach (var child in element.Children) { Write(writer, child, depth + 1); }
            writer.EndMaster();
        }
        else if (element.Value is BinarySegment binary)
        {
            writer.WriteBinary(element.Identifier, binary, Width(element, binary.Length));
        }
        else { WriteScalar(writer, element); }
    }

    private (int Value, int Size) ScalarWidths(EbmlElement element)
    {
        // RFC 9559 section 4.4: numeric defaults must have nonempty payloads.
        var width = element.Value is string ? -1 : 8;
        if (element.Value is ulong number && !_relocatedPositions.Contains(element))
        {
            width = 1;
            for (var rest = number >> 8; rest != 0; rest >>= 8) { width++; }
        }
        var payload = EbmlWriter.GetEncodedLength(element.Identifier, element.Type, element.Value!, _schema, width, 8)
            - element.Identifier.Length - 8;
        return (width, Width(element, payload));
    }

    private void WriteScalar(EbmlWriter writer, EbmlElement element)
    {
        var widths = ScalarWidths(element);
        switch (element.Value)
        {
            case ulong number:
                writer.WriteUnsignedInteger(element.Identifier, number, widths.Value, widths.Size); break;
            case long number when element.Type == EbmlElementType.Date:
                writer.WriteDate(element.Identifier, number, widths.Value, widths.Size); break;
            case long number:
                writer.WriteSignedInteger(element.Identifier, number, widths.Value, widths.Size); break;
            case double number:
                writer.WriteFloat(element.Identifier, number, widths.Value, widths.Size); break;
            case string text when element.Type == EbmlElementType.AsciiString:
                writer.WriteAsciiString(element.Identifier, text, widths.Value, widths.Size); break;
            case string text:
                writer.WriteUtf8String(element.Identifier, text, widths.Value, widths.Size); break;
        }
    }
    private static int Width(EbmlElement element, long payload) => element.IsUnknownSize ? Math.Max(1, element.SizeLength == 0 ? 8 : element.SizeLength) :
        Math.Max(element.SizeLength, VInt.EncodeSize((ulong)payload).Length);
    private static long Add(long left, long right, long offset)
    {
        if (right > 0x00FFFFFFFFFFFFFEL - left)
        { throw new ContentFormatException(ErrorMessages.SizeLimit, ContentFormatErrorCode.LimitExceeded, offset); }
        return left + right;
    }

    private sealed record Snapshot(object? Value, EbmlElement[] Children, int SizeLength, bool UnknownSize, BinarySegment Bytes);
}
