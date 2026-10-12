using System;

namespace Assimalign.Cohesion.Sdk.Database.Tests;

public sealed record DefaultConstantRow(
    string Text, string Empty, char Character, sbyte SByte, byte Byte, short Int16,
    int Int32, long Int64, float Float32, double Float64, decimal Decimal,
    decimal DecimalFromInteger, float Float32FromCharacter, double Float64FromCharacter,
    decimal DecimalFromCharacter, bool True, bool False, int? OptionalNumber,
    int? OptionalValue, string? OptionalText, DefaultStatus Enum, DefaultStatus EnumCast,
    DefaultSByte EnumSByte, DefaultByte EnumByte, DefaultInt16 EnumInt16,
    DefaultUInt16 EnumUInt16, DefaultUInt32 EnumUInt32, DefaultInt64 EnumInt64,
    DefaultUInt64 EnumUInt64, DateOnly RawDate);

public enum DefaultStatus { Pending = 2 }
public enum DefaultSByte : sbyte { Negative = -8 }
public enum DefaultByte : byte { Maximum = byte.MaxValue }
public enum DefaultInt16 : short { Negative = -300 }
public enum DefaultUInt16 : ushort { Maximum = ushort.MaxValue }
public enum DefaultUInt32 : uint { Maximum = uint.MaxValue }
public enum DefaultInt64 : long { Negative = -7000000000 }
public enum DefaultUInt64 : ulong { Maximum = ulong.MaxValue }
