using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;

using Assimalign.Cohesion.Amqp;
using Assimalign.Cohesion.Amqp.Connections.Internal;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

/// <summary>
/// The type-system corpus: the codec's supported surface of the AMQP 1.0 type system (Part 1), written as the
/// types it supports and an encoding for every constructor each one has.
/// </summary>
/// <remarks>
/// <para>
/// The corpus is the executable form of the surface decision in the project's design document. Each type
/// names the constructors the specification defines for it, the CLR type a decoded value has, and the CLR
/// types the encoder writes as it; a case gives an encoding, the value it stands for, and which way it runs.
/// </para>
/// <para>
/// A refused case names the exact message the codec gives, so a rule that starts refusing for the wrong
/// reason fails as surely as one that stops refusing.
/// </para>
/// </remarks>
internal static class AmqpTypeSystemCorpus
{
    private const AmqpTypeEncodingDirection roundTrip = AmqpTypeEncodingDirection.RoundTrip;
    private const AmqpTypeEncodingDirection decodeOnly = AmqpTypeEncodingDirection.DecodeOnly;
    private const AmqpTypeEncodingDirection encodeOnly = AmqpTypeEncodingDirection.EncodeOnly;
    private const AmqpTypeEncodingDirection decodeRefused = AmqpTypeEncodingDirection.DecodeRefused;
    private const AmqpTypeEncodingDirection encodeRefused = AmqpTypeEncodingDirection.EncodeRefused;

    /// <summary>
    /// Gets the types in the supported surface: every primitive type of Part 1, section 1.6, and described values.
    /// </summary>
    public static IReadOnlyList<AmqpTypeDefinition> Types { get; } =
    [
        new("null", "Part 1, section 1.6.1", [0x40], null, []),
        new("boolean", "Part 1, section 1.6.2", [0x56, 0x41, 0x42], typeof(bool), [typeof(bool)]),
        new("ubyte", "Part 1, section 1.6.3", [0x50], typeof(byte), [typeof(byte)]),
        new("ushort", "Part 1, section 1.6.4", [0x60], typeof(ushort), [typeof(ushort)]),
        new("uint", "Part 1, section 1.6.5", [0x70, 0x52, 0x43], typeof(uint), [typeof(uint)]),
        new("ulong", "Part 1, section 1.6.6", [0x80, 0x53, 0x44], typeof(ulong), [typeof(ulong)]),
        new("byte", "Part 1, section 1.6.7", [0x51], typeof(sbyte), [typeof(sbyte)]),
        new("short", "Part 1, section 1.6.8", [0x61], typeof(short), [typeof(short)]),
        new("int", "Part 1, section 1.6.9", [0x71, 0x54], typeof(int), [typeof(int)]),
        new("long", "Part 1, section 1.6.10", [0x81, 0x55], typeof(long), [typeof(long)]),
        new("float", "Part 1, section 1.6.11", [0x72], typeof(float), [typeof(float)]),
        new("double", "Part 1, section 1.6.12", [0x82], typeof(double), [typeof(double)]),
        new("decimal32", "Part 1, section 1.6.13", [0x74], typeof(AmqpDecimal32), [typeof(AmqpDecimal32)]),
        new("decimal64", "Part 1, section 1.6.14", [0x84], typeof(AmqpDecimal64), [typeof(AmqpDecimal64)]),
        new("decimal128", "Part 1, section 1.6.15", [0x94], typeof(AmqpDecimal128), [typeof(AmqpDecimal128)]),
        new("char", "Part 1, section 1.6.16", [0x73], typeof(Rune), [typeof(Rune), typeof(char)]),
        new("timestamp", "Part 1, section 1.6.17", [0x83], typeof(DateTimeOffset), [typeof(DateTimeOffset), typeof(DateTime)]),
        new("uuid", "Part 1, section 1.6.18", [0x98], typeof(Guid), [typeof(Guid)]),
        new("binary", "Part 1, section 1.6.19", [0xa0, 0xb0], typeof(byte[]), [typeof(byte[]), typeof(ReadOnlyMemory<byte>), typeof(Memory<byte>), typeof(ArraySegment<byte>), typeof(AmqpDeliveryTag)]),
        new("string", "Part 1, section 1.6.20", [0xa1, 0xb1], typeof(string), [typeof(string)]),
        new("symbol", "Part 1, section 1.6.21", [0xa3, 0xb3], typeof(AmqpSymbol), [typeof(AmqpSymbol)]),
        new("list", "Part 1, section 1.6.22", [0x45, 0xc0, 0xd0], typeof(List<object>), [typeof(List<object>), typeof(object[]), typeof(ArrayList)]),
        new("map", "Part 1, section 1.6.23", [0xc1, 0xd1], typeof(Dictionary<object, object>), [typeof(Dictionary<object, object>), typeof(Dictionary<string, object>), typeof(Dictionary<AmqpSymbol, object>), typeof(Hashtable)]),
        new("array", "Part 1, section 1.6.24", [0xe0, 0xf0], null,
        [
            typeof(AmqpSymbol[]), typeof(string[]), typeof(byte[][]), typeof(bool[]), typeof(sbyte[]), typeof(short[]), typeof(ushort[]),
            typeof(int[]), typeof(uint[]), typeof(long[]), typeof(ulong[]), typeof(float[]), typeof(double[]), typeof(AmqpDecimal32[]),
            typeof(AmqpDecimal64[]), typeof(AmqpDecimal128[]), typeof(Rune[]), typeof(char[]), typeof(DateTimeOffset[]), typeof(DateTime[]),
            typeof(Guid[]), typeof(AmqpArray)
        ]),
        new("described", "Part 1, sections 1.2 and 1.5", [0x00], null, [typeof(AmqpDescribedValue)])
    ];

    /// <summary>
    /// Gets the cases.
    /// </summary>
    public static IReadOnlyList<AmqpTypeEncodingCase> Cases { get; } =
    [
        new("null", "null", "40", null, roundTrip),

        new("true", "boolean", "41", true, roundTrip),
        new("false", "boolean", "42", false, roundTrip),
        new("boolean holding 1 in an octet", "boolean", "5601", true, decodeOnly),
        new("boolean holding 0 in an octet", "boolean", "5600", false, decodeOnly),
        new("boolean holding 2 in an octet", "boolean", "5602", null, decodeRefused, ErrorMessages.BooleanInvalid),

        new("ubyte 0", "ubyte", "5000", (byte)0, roundTrip),
        new("ubyte 255", "ubyte", "50ff", byte.MaxValue, roundTrip),
        new("ubyte without its octet", "ubyte", "50", null, decodeRefused, ErrorMessages.ValueTruncated),

        new("ushort 0x1234", "ushort", "601234", (ushort)0x1234, roundTrip),
        new("ushort 65535", "ushort", "60ffff", ushort.MaxValue, roundTrip),

        new("uint0", "uint", "43", 0u, roundTrip),
        new("smalluint 255", "uint", "52ff", 255u, roundTrip),
        new("uint 256", "uint", "7000000100", 256u, roundTrip),
        new("uint maximum", "uint", "70ffffffff", uint.MaxValue, roundTrip),
        new("smalluint 0", "uint", "5200", 0u, decodeOnly),
        new("uint 1 in four octets", "uint", "7000000001", 1u, decodeOnly),
        new("uint with three of its four octets", "uint", "70000001", null, decodeRefused, ErrorMessages.ValueTruncated),

        new("ulong0", "ulong", "44", 0ul, roundTrip),
        new("smallulong 255", "ulong", "53ff", 255ul, roundTrip),
        new("ulong 256", "ulong", "800000000000000100", 256ul, roundTrip),
        new("ulong maximum", "ulong", "80ffffffffffffffff", ulong.MaxValue, roundTrip),
        new("smallulong 0", "ulong", "5300", 0ul, decodeOnly),
        new("ulong 1 in eight octets", "ulong", "800000000000000001", 1ul, decodeOnly),

        new("byte -128", "byte", "5180", sbyte.MinValue, roundTrip),
        new("byte 127", "byte", "517f", sbyte.MaxValue, roundTrip),

        new("short -32768", "short", "618000", short.MinValue, roundTrip),
        new("short 32767", "short", "617fff", short.MaxValue, roundTrip),

        new("smallint -128", "int", "5480", -128, roundTrip),
        new("smallint 127", "int", "547f", 127, roundTrip),
        new("int 128", "int", "7100000080", 128, roundTrip),
        new("int -129", "int", "71ffffff7f", -129, roundTrip),
        new("int minimum", "int", "7180000000", int.MinValue, roundTrip),
        new("int -1 in four octets", "int", "71ffffffff", -1, decodeOnly),

        new("smalllong -1", "long", "55ff", -1L, roundTrip),
        new("long 128", "long", "810000000000000080", 128L, roundTrip),
        new("long minimum", "long", "818000000000000000", long.MinValue, roundTrip),
        new("long 1 in eight octets", "long", "810000000000000001", 1L, decodeOnly),

        new("float 1.5", "float", "723fc00000", 1.5f, roundTrip),
        new("float negative infinity", "float", "72ff800000", float.NegativeInfinity, roundTrip),
        new("float quiet NaN, bit for bit", "float", "727fc00000", BitConverter.Int32BitsToSingle(0x7fc00000), roundTrip),

        new("double 1.5", "double", "823ff8000000000000", 1.5d, roundTrip),
        new("double negative zero, bit for bit", "double", "828000000000000000", -0.0d, roundTrip),

        new("decimal32 bits", "decimal32", "7422500000", new AmqpDecimal32(0x22500000u), roundTrip),
        new("decimal64 bits", "decimal64", "842238000000000000", new AmqpDecimal64(0x2238000000000000ul), roundTrip),
        new("decimal128 bits", "decimal128", "9422080000000000000000000000000001", new AmqpDecimal128(new UInt128(0x2208000000000000ul, 1ul)), roundTrip),

        new("char A", "char", "7300000041", new Rune('A'), roundTrip),
        new("char outside the basic multilingual plane", "char", "730001f600", new Rune(0x1F600), roundTrip),
        new("char from a char", "char", "73000000e9", 'é', encodeOnly),
        new("char that is a surrogate", "char", "730000d800", null, decodeRefused, ErrorMessages.CharInvalid),
        new("char past U+10FFFF", "char", "7300110000", null, decodeRefused, ErrorMessages.CharInvalid),
        new("char from half of a surrogate pair", "char", string.Empty, '\udc00', encodeRefused, Format(ErrorMessages.CharSurrogate, 0xDC00)),

        new("timestamp at the epoch", "timestamp", "830000000000000000", DateTimeOffset.UnixEpoch, roundTrip),
        new("timestamp before the epoch", "timestamp", "83ffffffffffffffff", DateTimeOffset.FromUnixTimeMilliseconds(-1), roundTrip),
        new("timestamp from a UTC date and time", "timestamp", "830000000000000001", new DateTime(1970, 1, 1, 0, 0, 0, 1, DateTimeKind.Utc), encodeOnly),
        new("timestamp past the year 9999", "timestamp", "837fffffffffffffff", null, decodeRefused, ErrorMessages.TimestampOutOfRange),

        new("uuid in network order", "uuid", "9800112233445566778899aabbccddeeff", new Guid("00112233-4455-6677-8899-aabbccddeeff"), roundTrip),

        new("vbin8", "binary", "a0020102", new byte[] { 1, 2 }, roundTrip),
        new("vbin8 empty", "binary", "a000", Array.Empty<byte>(), roundTrip),
        new("vbin32 holding two octets", "binary", "b0000000020102", new byte[] { 1, 2 }, decodeOnly),
        new("binary from read-only memory", "binary", "a0020102", new ReadOnlyMemory<byte>([1, 2]), encodeOnly),
        new("binary from memory", "binary", "a0020102", new Memory<byte>([1, 2]), encodeOnly),
        new("binary from an array segment", "binary", "a0020102", new ArraySegment<byte>([0, 1, 2], 1, 2), encodeOnly),
        new("binary from a delivery tag", "binary", "a00109", AmqpDeliveryTag.Create(new byte[] { 9 }), encodeOnly),
        new("vbin8 longer than its octets", "binary", "a00501", null, decodeRefused, ErrorMessages.ValueTruncated),

        new("str8", "string", "a10161", "a", roundTrip),
        new("str8 empty", "string", "a100", string.Empty, roundTrip),
        new("str8 with a three-octet character", "string", "a103e282ac", "€", roundTrip),
        new("str8 with a supplementary character", "string", "a104f09f9880", "😀", roundTrip),
        new("str32 holding one octet", "string", "b10000000161", "a", decodeOnly),
        new("str8 that is not UTF-8", "string", "a101ff", null, decodeRefused, ErrorMessages.StringNotUtf8),
        new("str8 with an encoded surrogate", "string", "a103eda080", null, decodeRefused, ErrorMessages.StringNotUtf8),
        new("str8 with an overlong encoding", "string", "a102c0af", null, decodeRefused, ErrorMessages.StringNotUtf8),
        new("string with half of a surrogate pair", "string", string.Empty, new string(['a', '\ud800']), encodeRefused, Format(ErrorMessages.StringSurrogateUnpaired, 1)),

        new("sym8", "symbol", "a3026162", new AmqpSymbol("ab"), roundTrip),
        new("sym32 holding two octets", "symbol", "b3000000026162", new AmqpSymbol("ab"), decodeOnly),
        new("sym8 outside seven-bit ASCII", "symbol", "a30180", null, decodeRefused, ErrorMessages.SymbolNotAscii),

        new("list0", "list", "45", new List<object?>(), roundTrip),
        new("list8", "list", "c006025401a10161", new List<object?> { 1, "a" }, roundTrip),
        new("list8 empty", "list", "c00100", new List<object?>(), decodeOnly),
        new("list32 holding two elements", "list", "d000000009000000025401a10161", new List<object?> { 1, "a" }, decodeOnly),
        new("list from an object array", "list", "c006025401a10161", new object?[] { 1, "a" }, encodeOnly),
        new("list from a non-generic list", "list", "c006025401a10161", new ArrayList { 1, "a" }, encodeOnly),
        new("list8 counting more elements than its octets carry", "list", "c0020540", null, decodeRefused, ErrorMessages.CompoundCountInvalid),

        new("map8", "map", "c10602a101615401", new Dictionary<object, object?> { ["a"] = 1 }, roundTrip),
        new("map8 empty", "map", "c10100", new Dictionary<object, object?>(), roundTrip),
        new("map32 holding one entry", "map", "d10000000900000002a101615401", new Dictionary<object, object?> { ["a"] = 1 }, decodeOnly),
        new("map from string keys", "map", "c10602a101615401", new Dictionary<string, object?> { ["a"] = 1 }, encodeOnly),
        new("map from symbol keys", "map", "c10502a3016140", new Dictionary<AmqpSymbol, object?> { ["a"] = null }, encodeOnly),
        new("map from a non-generic dictionary", "map", "c10602a101615401", new Hashtable { ["a"] = 1 }, encodeOnly),
        new("map8 with an odd count", "map", "c1020140", null, decodeRefused, ErrorMessages.MapElementCountOdd),
        new("map8 with a null key", "map", "c103024040", null, decodeRefused, ErrorMessages.MapKeyNull),
        new("map8 with a repeated key", "map", "c10704540140540140", null, decodeRefused, ErrorMessages.MapKeyDuplicate),
        new("map8 with a binary key and an array-of-byte key of the same octets", "map", "c10f04a00201025401e004025101025402", new Dictionary<object, object?> { [new byte[] { 1, 2 }] = 1, [new sbyte[] { 1, 2 }] = 2 }, roundTrip),

        new("array8 of int", "array", "e00a02710000000100000002", new int[] { 1, 2 }, roundTrip),
        new("array8 empty", "array", "e0020071", Array.Empty<int>(), roundTrip),
        new("array8 of smallint", "array", "e00402540102", new int[] { 1, 2 }, decodeOnly),
        new("array32 of int", "array", "f00000000d00000002710000000100000002", new int[] { 1, 2 }, decodeOnly),
        new("array8 of symbol", "array", "e00602a301610162", new AmqpSymbol[] { "a", "b" }, roundTrip),
        new("array8 of string", "array", "e00401a10161", new string[] { "a" }, roundTrip),
        new("array8 of binary", "array", "e00702a00101020203", new byte[][] { [1], [2, 3] }, roundTrip),
        new("array8 of boolean", "array", "e00402560100", new bool[] { true, false }, roundTrip),
        new("array8 of byte", "array", "e0040251ff7f", new sbyte[] { -1, sbyte.MaxValue }, roundTrip),
        new("array8 of short", "array", "e0040161ffff", new short[] { -1 }, roundTrip),
        new("array8 of ushort", "array", "e00401600001", new ushort[] { 1 }, roundTrip),
        new("array8 of uint", "array", "e006017000000001", new uint[] { 1 }, roundTrip),
        new("array8 of long", "array", "e00a01810000000000000001", new long[] { 1 }, roundTrip),
        new("array8 of ulong", "array", "e00a01800000000000000001", new ulong[] { 1 }, roundTrip),
        new("array8 of float", "array", "e00601723fc00000", new float[] { 1.5f }, roundTrip),
        new("array8 of double", "array", "e00a01823ff8000000000000", new double[] { 1.5d }, roundTrip),
        new("array8 of decimal32", "array", "e006017422500000", new AmqpDecimal32[] { new(0x22500000u) }, roundTrip),
        new("array8 of decimal64", "array", "e00a01842238000000000000", new AmqpDecimal64[] { new(0x2238000000000000ul) }, roundTrip),
        new("array8 of decimal128", "array", "e012019422080000000000000000000000000001", new AmqpDecimal128[] { new(new UInt128(0x2208000000000000ul, 1ul)) }, roundTrip),
        new("array8 of char", "array", "e00a02730000006100000062", new Rune[] { new('a'), new('b') }, roundTrip),
        new("array8 of char from chars", "array", "e00a02730000006100000062", new char[] { 'a', 'b' }, encodeOnly),
        new("array8 of timestamp", "array", "e00a01830000000000000001", new DateTimeOffset[] { DateTimeOffset.FromUnixTimeMilliseconds(1) }, roundTrip),
        new("array8 of timestamp from dates and times", "array", "e00a01830000000000000001", new DateTime[] { new(1970, 1, 1, 0, 0, 0, 1, DateTimeKind.Utc) }, encodeOnly),
        new("array8 of uuid", "array", "e012019800112233445566778899aabbccddeeff", new Guid[] { new("00112233-4455-6677-8899-aabbccddeeff") }, roundTrip),
        new("array8 empty, of uint0", "array", "e0020043", Array.Empty<uint>(), decodeOnly),
        new("array8 of ubyte", "array", "e00402500102", new AmqpArray(AmqpPrimitiveType.UByte, [(byte)1, (byte)2]), roundTrip),
        new("array8 of described strings", "array", "e00902005301a101610162", new AmqpArray(AmqpPrimitiveType.String, ["a", "b"], 1ul), roundTrip),
        new("array8 of described smalluints", "array", "e0080200a30178520102", new AmqpArray(AmqpPrimitiveType.UInt, [1u, 2u], new AmqpSymbol("x")), decodeOnly),
        new("array8 empty, of null", "array", "e0020040", new AmqpArray(AmqpPrimitiveType.Null, []), roundTrip),
        new("array8 of list8", "array", "e00b02c0030154010401a10161", new AmqpArray(AmqpPrimitiveType.List, [new List<object?> { 1 }, new List<object?> { "a" }]), roundTrip),
        new("array8 of list32", "array", "e01702d0000000060000000154010000000700000001a10161", new AmqpArray(AmqpPrimitiveType.List, [new List<object?> { 1 }, new List<object?> { "a" }]), decodeOnly),
        new("array8 of empty lists", "array", "e00602c001000100", new AmqpArray(AmqpPrimitiveType.List, [new List<object?>(), new List<object?>()]), roundTrip),
        new("array8 empty, of list8", "array", "e00200c0", new AmqpArray(AmqpPrimitiveType.List, []), roundTrip),
        new("array8 empty, of list0", "array", "e0020045", new AmqpArray(AmqpPrimitiveType.List, []), decodeOnly),
        new("array8 of map8", "array", "e00901c10602a1016b5401", new AmqpArray(AmqpPrimitiveType.Map, [new Dictionary<object, object?> { ["k"] = 1 }]), roundTrip),
        new("array8 of map32", "array", "e00f01d10000000900000002a1016b5401", new AmqpArray(AmqpPrimitiveType.Map, [new Dictionary<object, object?> { ["k"] = 1 }]), decodeOnly),
        new("array8 of array8", "array", "e00e02e0060171000000010401a10161", new AmqpArray(AmqpPrimitiveType.Array, [new int[] { 1 }, new string[] { "a" }]), roundTrip),
        new("array8 of array32", "array", "e01a02f0000000090000000171000000010000000700000001a10161", new AmqpArray(AmqpPrimitiveType.Array, [new int[] { 1 }, new string[] { "a" }]), decodeOnly),
        new("array8 of int from an AmqpArray", "array", "e00a02710000000100000002", new AmqpArray(AmqpPrimitiveType.Int, [1, 2]), encodeOnly),
        new("array8 of zero-width elements", "array", "e003024000", null, decodeRefused, ErrorMessages.ArrayElementWidthInvalid),
        new("array8 counting more elements than its octets carry", "array", "e003057100", null, decodeRefused, ErrorMessages.CompoundCountInvalid),
        new("array8 counting more uuids than its octets carry", "array", "e012029800112233445566778899aabbccddeeff", null, decodeRefused, ErrorMessages.CompoundCountInvalid),
        new("array8 empty, with a constructor Part 1 does not define", "array", "e0020099", null, decodeRefused, Format(ErrorMessages.FormatCodeUnsupported, 0x99)),
        new("array of string holding null", "array", string.Empty, new string?[] { "a", null }, encodeRefused, Format(ErrorMessages.ArrayElementNull, 1)),
        new("array of symbol holding a default symbol", "array", string.Empty, new AmqpSymbol[] { "a", default }, encodeRefused, Format(ErrorMessages.ArrayElementNull, 1)),
        new("array of binary holding null", "array", string.Empty, new byte[]?[] { [1], null }, encodeRefused, Format(ErrorMessages.ArrayElementNull, 1)),
        new("array of char holding half of a surrogate pair", "array", string.Empty, new char[] { 'a', '\ud800' }, encodeRefused, Format(ErrorMessages.CharSurrogate, 0xD800)),
        new("array of int holding a long", "array", string.Empty, new AmqpArray(AmqpPrimitiveType.Int, [1, 2L]), encodeRefused, Format(ErrorMessages.ArrayElementTypeMismatch, 1, typeof(long), AmqpPrimitiveType.Int)),
        new("array of uint holding an int", "array", string.Empty, new AmqpArray(AmqpPrimitiveType.UInt, [1u, 2]), encodeRefused, Format(ErrorMessages.ArrayElementTypeMismatch, 1, typeof(int), AmqpPrimitiveType.UInt)),
        new("array of binary holding an array of byte", "array", string.Empty, new AmqpArray(AmqpPrimitiveType.Binary, [new byte[] { 1 }, new sbyte[] { 1 }]), encodeRefused, Format(ErrorMessages.ArrayElementTypeMismatch, 1, typeof(sbyte[]), AmqpPrimitiveType.Binary)),
        new("array of lists holding a map", "array", string.Empty, new AmqpArray(AmqpPrimitiveType.List, [new List<object?>(), new Dictionary<object, object?>()]), encodeRefused, Format(ErrorMessages.ArrayElementTypeMismatch, 1, typeof(Dictionary<object, object?>), AmqpPrimitiveType.List)),
        new("array of lists holding a described list", "array", string.Empty, new AmqpArray(AmqpPrimitiveType.List, [new AmqpDescribedValue(1ul, new List<object?>())]), encodeRefused, Format(ErrorMessages.ArrayElementTypeMismatch, 0, typeof(AmqpDescribedValue), AmqpPrimitiveType.List)),
        new("array of ubyte holding null", "array", string.Empty, new AmqpArray(AmqpPrimitiveType.UByte, [(byte)1, null]), encodeRefused, Format(ErrorMessages.ArrayElementNull, 1)),
        new("array of null holding an element", "array", string.Empty, new AmqpArray(AmqpPrimitiveType.Null, [null]), encodeRefused, ErrorMessages.ArrayOfNullNotEmpty),

        new("described with a numeric descriptor", "described", "005301a10161", new AmqpDescribedValue(1ul, "a"), roundTrip),
        new("described with a symbolic descriptor", "described", "00a303783a7940", new AmqpDescribedValue(new AmqpSymbol("x:y"), null), roundTrip),
        new("described with an eight-octet descriptor", "described", "00800000000000000001a10161", new AmqpDescribedValue(1ul, "a"), decodeOnly),
        new("described with a descriptor that is not a ulong or symbol", "described", "004040", null, decodeRefused, ErrorMessages.DescriptorInvalid)
    ];

    /// <summary>
    /// Gets CLR values outside the supported surface, which the encoder refuses rather than guess at.
    /// </summary>
    public static IReadOnlyList<object> Unsupported { get; } =
    [
        1.5m,
        TimeSpan.FromSeconds(1),
        (Half)1,
        (Int128)1,
        (UInt128)1,
        new BigInteger(1),
        new DateOnly(2024, 1, 1),
        new TimeOnly(1, 0),
        DayOfWeek.Monday,
        (nint)1,
        (nuint)1,
        new Uri("amqp://localhost"),
        new HashSet<object?> { 1 },
        new object()
    ];

    /// <summary>
    /// Finds a case by its name.
    /// </summary>
    public static AmqpTypeEncodingCase Find(string name)
    {
        return Cases.Single(entry => entry.Name == name);
    }

    /// <summary>
    /// Finds a type by its name.
    /// </summary>
    public static AmqpTypeDefinition FindType(string name)
    {
        return Types.Single(type => type.Name == name);
    }

    private static string Format(string format, params object[] arguments)
    {
        return string.Format(CultureInfo.CurrentCulture, format, arguments);
    }
}
