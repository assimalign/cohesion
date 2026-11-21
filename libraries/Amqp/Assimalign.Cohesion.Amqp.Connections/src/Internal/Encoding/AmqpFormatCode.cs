namespace Assimalign.Cohesion.Amqp.Connections.Internal;

/// <summary>
/// The format codes of the AMQP 1.0 type system (Part 1, section 1.6).
/// </summary>
internal static class AmqpFormatCode
{
    public const byte Described = 0x00;
    public const byte Null = 0x40;
    public const byte Boolean = 0x56;
    public const byte BooleanTrue = 0x41;
    public const byte BooleanFalse = 0x42;
    public const byte UByte = 0x50;
    public const byte UShort = 0x60;
    public const byte UInt = 0x70;
    public const byte SmallUInt = 0x52;
    public const byte UInt0 = 0x43;
    public const byte ULong = 0x80;
    public const byte SmallULong = 0x53;
    public const byte ULong0 = 0x44;
    public const byte Byte = 0x51;
    public const byte Short = 0x61;
    public const byte Int = 0x71;
    public const byte SmallInt = 0x54;
    public const byte Long = 0x81;
    public const byte SmallLong = 0x55;
    public const byte Float = 0x72;
    public const byte Double = 0x82;
    public const byte Decimal32 = 0x74;
    public const byte Decimal64 = 0x84;
    public const byte Decimal128 = 0x94;
    public const byte Char = 0x73;
    public const byte Timestamp = 0x83;
    public const byte Uuid = 0x98;
    public const byte Binary8 = 0xa0;
    public const byte Binary32 = 0xb0;
    public const byte String8 = 0xa1;
    public const byte String32 = 0xb1;
    public const byte Symbol8 = 0xa3;
    public const byte Symbol32 = 0xb3;
    public const byte List0 = 0x45;
    public const byte List8 = 0xc0;
    public const byte List32 = 0xd0;
    public const byte Map8 = 0xc1;
    public const byte Map32 = 0xd1;
    public const byte Array8 = 0xe0;
    public const byte Array32 = 0xf0;
}
