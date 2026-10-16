using System;
using System.Collections.Generic;
using System.IO;

namespace Assimalign.Cohesion.Content.Ebml.Tests.TestObjects;

// All bytes are synthesized here from RFC rules or this package's VInt writer; no external fixtures.
internal static class Rfc8794Corpus
{
    internal static IEnumerable<object[]> Identifiers()
    {
        for (var width = 1; width <= 8; width++)
        {
            var marker = 1UL << (7 * width);
            var mask = marker - 1;
            yield return new object[] { $"RFC 8794 §5 zero data, width {width}", marker, false };
            yield return new object[] { $"RFC 8794 §5 reserved ones, width {width}", marker | mask, false };
            yield return new object[] { $"RFC 8794 §5 highest ID, width {width}", marker | (mask - 1), true };
            if (width > 1)
            {
                var previousMask = (1UL << (7 * (width - 1))) - 1;
                yield return new object[] { $"RFC 8794 §5 shortest reserved-boundary ID, width {width}", marker | previousMask, true };
                yield return new object[] { $"RFC 8794 §5 over-long ID, width {width}", marker | (previousMask - 1), false };
            }
        }
    }

    internal static IEnumerable<object[]> InvalidValues()
    {
        foreach (var type in new[] { EbmlElementType.UnsignedInteger, EbmlElementType.SignedInteger })
        {
            yield return new object[] { $"RFC 8794 §§7.1–7.2 nine-octet {type}", type, new byte[9] };
        }

        foreach (var length in new[] { 1, 2, 3, 5, 6, 7, 9 })
        {
            yield return new object[] { $"RFC 8794 §7.3 invalid float width {length}", EbmlElementType.Float, new byte[length] };
        }

        foreach (var length in new[] { 1, 4, 7, 9 })
        {
            yield return new object[] { $"RFC 8794 §7.6 invalid date width {length}", EbmlElementType.Date, new byte[length] };
        }

        foreach (var value in new byte[] { 0x1f, 0x7f, 0xff })
        {
            yield return new object[] { $"RFC 8794 §7.4 non-printable ASCII {value}", EbmlElementType.AsciiString, new byte[] { value } };
        }

        foreach (var bytes in new byte[][] { [0xC0, 0x80], [0xED, 0xA0, 0x80], [0xF4, 0x90, 0x80, 0x80], [0xE2, 0x82] })
        {
            yield return new object[] { "RFC 8794 §7.5 invalid UTF-8 " + Convert.ToHexString(bytes), EbmlElementType.Utf8String, bytes };
        }
    }

    internal static byte[] Element(ulong id, byte[] payload, int sizeWidth = 0)
    {
        using var output = new MemoryStream();
        VInt.FromEncoded(id).Write(output);
        VInt.EncodeSize((ulong)payload.Length, sizeWidth).Write(output);
        output.Write(payload);
        return output.ToArray();
    }

    internal static byte[] HeaderDocument(byte[] headerPayload, byte[]? body = null)
    {
        using var output = new MemoryStream();
        output.Write(Element(0x1A45DFA3, headerPayload));
        output.Write(body ?? new byte[] { 0xA0, 0x80 });
        return output.ToArray();
    }

    internal static IEnumerable<object[]> Headers()
    {
        var docType = Element(0x4282, "test"u8.ToArray());
        yield return new object[] { "RFC 8794 §11.2 defaults omitted", HeaderDocument(docType), ContentFormatErrorCode.Unspecified };
        yield return new object[] { "RFC 8794 §11.2.2 explicit empty default", HeaderDocument(Join(docType, Element(0x4286, []))), ContentFormatErrorCode.Unspecified };
        yield return new object[] { "RFC 8794 §11.2.6 missing DocType", HeaderDocument([]), ContentFormatErrorCode.InvalidStructure };
        yield return new object[] { "RFC 8794 §11.2.6 empty DocType", HeaderDocument(Element(0x4282, [])), ContentFormatErrorCode.InvalidValue };
        yield return new object[] { "RFC 8794 §11.2.6 NUL-only DocType", HeaderDocument(Element(0x4282, [0])), ContentFormatErrorCode.InvalidValue };
        yield return new object[] { "RFC 8794 §11.2.4 MaxIDLength below four", HeaderDocument(Join(docType, Element(0x42F2, [3]))), ContentFormatErrorCode.InvalidValue };
        yield return new object[] { "RFC 8794 §11.2.4 unsupported MaxIDLength", HeaderDocument(Join(docType, Element(0x42F2, [9]))), ContentFormatErrorCode.Unsupported };
        yield return new object[] { "RFC 8794 §11.2.5 zero MaxSizeLength", HeaderDocument(Join(docType, Element(0x42F3, [0]))), ContentFormatErrorCode.InvalidValue };
        yield return new object[] { "RFC 8794 §11.2.8 reader version exceeds writer", HeaderDocument(Join(docType, Element(0x4285, [2]))), ContentFormatErrorCode.InvalidValue };
        yield return new object[] { "RFC 8794 §11.2.3 unsupported EBML read version", HeaderDocument(Join(docType, Element(0x4286, [2]), Element(0x42F7, [2]))), ContentFormatErrorCode.Unsupported };
        yield return new object[] { "RFC 8794 §11.2.6 duplicate DocType", HeaderDocument(Join(docType, docType)), ContentFormatErrorCode.InvalidStructure };
        var extension = Element(0x4281, Join(Element(0x4283, "x"u8.ToArray()), Element(0x4284, [1])));
        yield return new object[] { "RFC 8794 §§11.2.9–11.2.11 extension", HeaderDocument(Join(docType, extension)), ContentFormatErrorCode.Unspecified };
        yield return new object[] { "RFC 8794 §11.2.10 duplicate extension name", HeaderDocument(Join(docType, extension, extension)), ContentFormatErrorCode.InvalidValue };
        yield return new object[] { "RFC 8794 §6.2 unknown body root", HeaderDocument(docType, [0xA0, 0xFF]), ContentFormatErrorCode.Unspecified };
    }

    internal static byte[] Join(params byte[][] values)
    {
        using var output = new MemoryStream();
        foreach (var value in values)
        {
            output.Write(value);
        }

        return output.ToArray();
    }
}
