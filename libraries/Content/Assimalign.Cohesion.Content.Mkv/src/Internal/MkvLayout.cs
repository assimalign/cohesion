using System;
using System.Collections.Generic;
using System.IO;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Ebml;

namespace Assimalign.Cohesion.Content.Mkv.Internal;

// EBML does not expose encoded lengths, original raw elements, or a raw-element write seam.
// Keep the workaround local: snapshot ranges, measure scalar leaves with its writer, and copy
// unchanged trees verbatim. No EBML headers are hand-parsed and binary payloads are never measured by reading.
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
            using var count = new MkvCountingStream();
            using var writer = EbmlWriter.Create(count, _schema, maxDepth: _maxDepth);
            writer.WriteElement(element); // Uses EBML's preserved raw scalar, including padding/width.
            length = count.Length;
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
        { return Add(binary.Length, element.Identifier.Length + Width(element, binary.Length), element.Offset); }
        using var count = new MkvCountingStream();
        WriteScalar(count, element);
        return count.Length;
    }

    private long PayloadLength(EbmlElement element, int depth)
    {
        long length = 0;
        foreach (var child in element.Children) { length = Add(length, Measure(child, depth + 1), element.Offset); }
        return length;
    }

    internal void Write(Stream destination, EbmlElement element, int depth = 0)
    {
        if (Unchanged(element)) { _original[element].Bytes.CopyTo(destination); return; }
        if (element.Type == EbmlElementType.MasterElement)
        {
            var payload = PayloadLength(element, depth);
            using (var header = EbmlWriter.Create(destination, _schema, maxDepth: _maxDepth))
            {
                // Emit only the precomputed master header. Each child owns its own writer/copy;
                // disposing EBML's writer deliberately does not finish an open master.
                header.BeginMaster(element.Identifier, element.IsUnknownSize ? null : payload, Width(element, payload));
            }
            foreach (var child in element.Children) { Write(destination, child, depth + 1); }
        }
        else if (element.Value is BinarySegment binary)
        {
            using var writer = EbmlWriter.Create(destination, _schema, maxDepth: _maxDepth);
            writer.WriteBinary(element.Identifier, binary, Width(element, binary.Length));
        }
        else { WriteScalar(destination, element); }
    }

    private void WriteScalar(Stream destination, EbmlElement element)
    {
        // The generic EBML writer uses empty payloads for defaults. RFC 9559 section 4.4
        // forbids that for nonzero numeric defaults: explicit widths avoid that core behavior.
        using var writer = EbmlWriter.Create(destination, _schema, maxDepth: _maxDepth);
        var sizeWidth = element.SizeLength;
        switch (element.Value)
        {
            case ulong number:
                var width = 1;
                for (var rest = number >> 8; rest != 0; rest >>= 8) { width++; }
                if (_relocatedPositions.Contains(element)) { width = 8; }
                writer.WriteUnsignedInteger(element.Identifier, number, width, sizeWidth); break;
            case long number when element.Type == EbmlElementType.Date:
                writer.WriteDate(element.Identifier, number, 8, sizeWidth); break;
            case long number:
                writer.WriteSignedInteger(element.Identifier, number, 8, sizeWidth); break;
            case double number:
                writer.WriteFloat(element.Identifier, number, 8, sizeWidth); break;
            case string text when element.Type == EbmlElementType.AsciiString:
                writer.WriteAsciiString(element.Identifier, text, sizeLength: GrowStringWidth(element, text.Length)); break;
            case string text:
                writer.WriteUtf8String(element.Identifier, text, sizeLength: GrowStringWidth(element, System.Text.Encoding.UTF8.GetByteCount(text))); break;
        }
    }

    private static int GrowStringWidth(EbmlElement element, int length) => Width(element, length);
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
