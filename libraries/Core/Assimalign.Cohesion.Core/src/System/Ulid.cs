#pragma warning disable CS0162, CS1574, CS9191, CS9195, CS8767, CS8765, CS8603

using System.Buffers;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Runtime.Intrinsics.X86;
using System.Runtime.Intrinsics;
using System.Globalization;

namespace System;

using Assimalign.Cohesion.Internal;

/// <summary>
/// Represents a Universally Unique Lexicographically Sortable Identifier (ULID).
/// Spec: https://github.com/ulid/spec
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 16)]
[DebuggerDisplay("{ToString(),nq}")]
[TypeConverter(typeof(UlidTypeConverter))]
//[System.Text.Json.Serialization.JsonConverter(typeof(Cysharp.Serialization.Json.UlidJsonConverter))]
public readonly partial struct Ulid :
    IEquatable<Ulid>,
    IComparable<Ulid>,
    IComparable,
    ISpanFormattable,
    ISpanParsable<Ulid>,
    IUtf8SpanFormattable
{
    // https://en.wikipedia.org/wiki/Base32
    static readonly char[] Base32Text = "0123456789ABCDEFGHJKMNPQRSTVWXYZ".ToCharArray();
    static readonly byte[] Base32Bytes = Encoding.UTF8.GetBytes(Base32Text);
    static readonly byte[] CharToBase32 = new byte[] { 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 255, 255, 255, 255, 255, 255, 255, 10, 11, 12, 13, 14, 15, 16, 17, 255, 18, 19, 255, 20, 21, 255, 22, 23, 24, 25, 26, 255, 27, 28, 29, 30, 31, 255, 255, 255, 255, 255, 255, 10, 11, 12, 13, 14, 15, 16, 17, 255, 18, 19, 255, 20, 21, 255, 22, 23, 24, 25, 26, 255, 27, 28, 29, 30, 31 };
    static readonly DateTimeOffset UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static readonly Ulid MinValue = new Ulid(UnixEpoch.ToUnixTimeMilliseconds(), new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
    public static readonly Ulid MaxValue = new Ulid(DateTimeOffset.MaxValue.ToUnixTimeMilliseconds(), new byte[] { 255, 255, 255, 255, 255, 255, 255, 255, 255, 255 });
    public static readonly Ulid Empty = new Ulid();

    // Core

    // Timestamp(48bits)
    [FieldOffset(0)] 
    private readonly byte _timestamp0;

    [FieldOffset(1)] 
    private readonly byte _timestamp1;

    [FieldOffset(2)] 
    private readonly byte _timestamp2;

    [FieldOffset(3)] 
    private readonly byte _timestamp3;

    [FieldOffset(4)] 
    private readonly byte _timestamp4;

    [FieldOffset(5)] 
    private readonly byte _timestamp5;

    // Randomness(80bits)
    [FieldOffset(6)] 
    private readonly byte _randomness0;

    [FieldOffset(7)] 
    private readonly byte _randomness1;

    [FieldOffset(8)] 
    private readonly byte _randomness2;

    [FieldOffset(9)] 
    private readonly byte _randomness3;

    [FieldOffset(10)] 
    private readonly byte _randomness4;

    [FieldOffset(11)] 
    private readonly byte _randomness5;

    [FieldOffset(12)] 
    private readonly byte _randomness6;

    [FieldOffset(13)] 
    private readonly byte _randomness7;

    [FieldOffset(14)] 
    private readonly byte _randomness8;

    [FieldOffset(15)] 
    private readonly byte _randomness9;


    [IgnoreDataMember]
    public byte[] Random => new byte[]
    {
            _randomness0,
            _randomness1,
            _randomness2,
            _randomness3,
            _randomness4,
            _randomness5,
            _randomness6,
            _randomness7,
            _randomness8,
            _randomness9,
    };

    [IgnoreDataMember]
    public DateTimeOffset Time
    {
        get
        {
            if (BitConverter.IsLittleEndian)
            {
                // |A|B|C|D|E|F|... -> |F|E|D|C|B|A|0|0|

                // Lower |A|B|C|D| -> |D|C|B|A|
                // Upper |E|F| -> |F|E|
                // Time  |F|E| + |0|0|D|C|B|A|
                var lower = Unsafe.ReadUnaligned<uint>(ref Unsafe.AsRef(_timestamp0));
                var upper = Unsafe.ReadUnaligned<ushort>(ref Unsafe.AsRef(_timestamp4));
                var time = (long)BinaryPrimitives.ReverseEndianness(upper) + (((long)BinaryPrimitives.ReverseEndianness(lower)) << 16);
                return DateTimeOffset.FromUnixTimeMilliseconds(time);
            }
            else
            {
                // |A|B|C|D|E|F|... -> |0|0|A|B|C|D|E|F|

                // Upper |A|B|C|D|
                // Lower |E|F|
                // Time  |A|B|C|C|0|0| + |E|F|
                var upper = Unsafe.ReadUnaligned<uint>(ref Unsafe.AsRef(_timestamp0));
                var lower = Unsafe.ReadUnaligned<ushort>(ref Unsafe.AsRef(_timestamp4));
                var time = ((long)upper << 16) + (long)lower;
                return DateTimeOffset.FromUnixTimeMilliseconds(time);
            }
        }
    }

    #region Constructors

    internal Ulid(long timestampMilliseconds, XorShift64 random)
        : this()
    {
        unsafe
        {
            ref var firstByte = ref Unsafe.AsRef<byte>(Unsafe.AsPointer(ref timestampMilliseconds));
            if (BitConverter.IsLittleEndian)
            {
                // Get memory in stack and copy to ulid(Little->Big reverse order).
                _timestamp0 = Unsafe.Add(ref firstByte, 5);
                _timestamp1 = Unsafe.Add(ref firstByte, 4);
                _timestamp2 = Unsafe.Add(ref firstByte, 3);
                _timestamp3 = Unsafe.Add(ref firstByte, 2);
                _timestamp4 = Unsafe.Add(ref firstByte, 1);
                _timestamp5 = Unsafe.Add(ref firstByte, 0);
            }
            else
            {
                _timestamp0 = Unsafe.Add(ref firstByte, 2);
                _timestamp1 = Unsafe.Add(ref firstByte, 3);
                _timestamp2 = Unsafe.Add(ref firstByte, 4);
                _timestamp3 = Unsafe.Add(ref firstByte, 5);
                _timestamp4 = Unsafe.Add(ref firstByte, 6);
                _timestamp5 = Unsafe.Add(ref firstByte, 7);
            }
        }

        // Get first byte of randomness from Ulid Struct.
        Unsafe.WriteUnaligned(ref _randomness0, random.Next()); // randomness0~7(but use 0~1 only)
        Unsafe.WriteUnaligned(ref _randomness2, random.Next()); // randomness2~9
    }
    internal Ulid(long timestampMilliseconds, ReadOnlySpan<byte> randomness)
        : this()
    {
        unsafe
        {
            ref var firstByte = ref Unsafe.AsRef<byte>(Unsafe.AsPointer(ref timestampMilliseconds));
            if (BitConverter.IsLittleEndian)
            {
                // Get memory in stack and copy to ulid(Little->Big reverse order).
                _timestamp0 = Unsafe.Add(ref firstByte, 5);
                _timestamp1 = Unsafe.Add(ref firstByte, 4);
                _timestamp2 = Unsafe.Add(ref firstByte, 3);
                _timestamp3 = Unsafe.Add(ref firstByte, 2);
                _timestamp4 = Unsafe.Add(ref firstByte, 1);
                _timestamp5 = Unsafe.Add(ref firstByte, 0);
            }
            else
            {
                _timestamp0 = Unsafe.Add(ref firstByte, 2);
                _timestamp1 = Unsafe.Add(ref firstByte, 3);
                _timestamp2 = Unsafe.Add(ref firstByte, 4);
                _timestamp3 = Unsafe.Add(ref firstByte, 5);
                _timestamp4 = Unsafe.Add(ref firstByte, 6);
                _timestamp5 = Unsafe.Add(ref firstByte, 7);
            }
        }

        ref var src = ref MemoryMarshal.GetReference(randomness); // length = 10
        _randomness0 = randomness[0];
        _randomness1 = randomness[1];
        Unsafe.WriteUnaligned(ref _randomness2, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref src, 2))); // randomness2~randomness9
    }
    internal Ulid(ReadOnlySpan<char> base32)
    {
        // unroll-code is based on NUlid.

        _randomness9 = (byte)((CharToBase32[base32[24]] << 5) | CharToBase32[base32[25]]); // eliminate bounds-check of span

        _timestamp0 = (byte)((CharToBase32[base32[0]] << 5) | CharToBase32[base32[1]]);
        _timestamp1 = (byte)((CharToBase32[base32[2]] << 3) | (CharToBase32[base32[3]] >> 2));
        _timestamp2 = (byte)((CharToBase32[base32[3]] << 6) | (CharToBase32[base32[4]] << 1) | (CharToBase32[base32[5]] >> 4));
        _timestamp3 = (byte)((CharToBase32[base32[5]] << 4) | (CharToBase32[base32[6]] >> 1));
        _timestamp4 = (byte)((CharToBase32[base32[6]] << 7) | (CharToBase32[base32[7]] << 2) | (CharToBase32[base32[8]] >> 3));
        _timestamp5 = (byte)((CharToBase32[base32[8]] << 5) | CharToBase32[base32[9]]);

        _randomness0 = (byte)((CharToBase32[base32[10]] << 3) | (CharToBase32[base32[11]] >> 2));
        _randomness1 = (byte)((CharToBase32[base32[11]] << 6) | (CharToBase32[base32[12]] << 1) | (CharToBase32[base32[13]] >> 4));
        _randomness2 = (byte)((CharToBase32[base32[13]] << 4) | (CharToBase32[base32[14]] >> 1));
        _randomness3 = (byte)((CharToBase32[base32[14]] << 7) | (CharToBase32[base32[15]] << 2) | (CharToBase32[base32[16]] >> 3));
        _randomness4 = (byte)((CharToBase32[base32[16]] << 5) | CharToBase32[base32[17]]);
        _randomness5 = (byte)((CharToBase32[base32[18]] << 3) | CharToBase32[base32[19]] >> 2);
        _randomness6 = (byte)((CharToBase32[base32[19]] << 6) | (CharToBase32[base32[20]] << 1) | (CharToBase32[base32[21]] >> 4));
        _randomness7 = (byte)((CharToBase32[base32[21]] << 4) | (CharToBase32[base32[22]] >> 1));
        _randomness8 = (byte)((CharToBase32[base32[22]] << 7) | (CharToBase32[base32[23]] << 2) | (CharToBase32[base32[24]] >> 3));
    }

    public Ulid(ReadOnlySpan<byte> bytes) : this()
    {
        ArgumentException.ThrowIf(bytes.Length != 16, "Invalid bytes length, length:" + bytes.Length);

        ref var src = ref MemoryMarshal.GetReference(bytes);
        Unsafe.WriteUnaligned(ref _timestamp0, Unsafe.ReadUnaligned<ulong>(ref src)); // timestamp0~randomness1
        Unsafe.WriteUnaligned(ref _randomness2, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref src, 8))); // randomness2~randomness9
    }

    // HACK: We assume the layout of a Guid is the following:
    // Int32, Int16, Int16, Int8, Int8, Int8, Int8, Int8, Int8, Int8, Int8
    // source: https://github.com/dotnet/runtime/blob/4f9ae42d861fcb4be2fcd5d3d55d5f227d30e723/src/libraries/System.Private.CoreLib/src/System/Guid.cs
    public Ulid(Guid guid)
    {
        if (IsVector128Supported && BitConverter.IsLittleEndian)
        {
            var vector = Unsafe.As<Guid, Vector128<byte>>(ref guid);
            var shuffled = Shuffle(vector, Vector128.Create((byte)3, 2, 1, 0, 5, 4, 7, 6, 8, 9, 10, 11, 12, 13, 14, 15));

            this = Unsafe.As<Vector128<byte>, Ulid>(ref shuffled);
            return;
        }

        Span<byte> buf = stackalloc byte[16];

        if (BitConverter.IsLittleEndian)
        {
            // |A|B|C|D|E|F|G|H|I|J|K|L|M|N|O|P|
            // |D|C|B|A|...
            //      ...|F|E|H|G|...
            //              ...|I|J|K|L|M|N|O|P|
            ref var ptr = ref Unsafe.As<Guid, uint>(ref guid);
            var lower = BinaryPrimitives.ReverseEndianness(ptr);
            MemoryMarshal.Write(buf, ref lower);

            ptr = ref Unsafe.Add(ref ptr, 1);
            var upper = ((ptr & 0x00_FF_00_FF) << 8) | ((ptr & 0xFF_00_FF_00) >> 8);
            MemoryMarshal.Write(buf.Slice(4), ref upper);

            ref var upperBytes = ref Unsafe.As<uint, ulong>(ref Unsafe.Add(ref ptr, 1));
            MemoryMarshal.Write(buf.Slice(8), ref upperBytes);
        }
        else
        {
            MemoryMarshal.Write(buf, ref guid);
        }

        this = MemoryMarshal.Read<Ulid>(buf);
    }

    #endregion

    public static Ulid NewUlid()
    {
        return new Ulid(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), RandomProvider.GetXorShift64());
    }

    public static Ulid NewUlid(DateTimeOffset timestamp)
    {
        return new Ulid(timestamp.ToUnixTimeMilliseconds(), RandomProvider.GetXorShift64());
    }

    public static Ulid NewUlid(DateTimeOffset timestamp, ReadOnlySpan<byte> randomness)
    {
        if (randomness.Length != 10)
        {
            throw new ArgumentException("invalid randomness length, length:" + randomness.Length);
        }
        return new Ulid(timestamp.ToUnixTimeMilliseconds(), randomness);
    }

    public static Ulid Parse(string base32)
    {
        return Parse(base32.AsSpan());
    }

    public static Ulid Parse(ReadOnlySpan<char> base32)
    {
        if (base32.Length != 26)
        {
            throw new ArgumentException("invalid base32 length, length:" + base32.Length);
        }
        return new Ulid(base32);
    }

    public static Ulid Parse(ReadOnlySpan<byte> base32)
    {
        if (!TryParse(base32, out var ulid))
        {
            throw new ArgumentException("invalid base32 length, length:" + base32.Length);
        }
        return ulid;
    }

    public static bool TryParse(string base32, out Ulid ulid)
    {
        return TryParse(base32.AsSpan(), out ulid);
    }

    public static bool TryParse(ReadOnlySpan<char> base32, out Ulid ulid)
    {
        if (base32.Length != 26)
        {
            ulid = default(Ulid);
            return false;
        }

        try
        {
            ulid = new Ulid(base32);
            return true;
        }
        catch
        {
            ulid = default(Ulid);
            return false;
        }
    }

    public static bool TryParse(ReadOnlySpan<byte> base32, out Ulid ulid)
    {
        if (base32.Length != 26)
        {
            ulid = default(Ulid);
            return false;
        }

        try
        {
            ulid = ParseCore(base32);
            return true;
        }
        catch
        {
            ulid = default(Ulid);
            return false;
        }
    }

    static Ulid ParseCore(ReadOnlySpan<byte> base32)
    {
        if (base32.Length != 26)
        {
            throw new ArgumentException("invalid base32 length, length:" + base32.Length);
        }

        var ulid = default(Ulid);

        Unsafe.Add(ref Unsafe.As<Ulid, byte>(ref ulid), 15) = (byte)((CharToBase32[base32[24]] << 5) | CharToBase32[base32[25]]);

        Unsafe.Add(ref Unsafe.As<Ulid, byte>(ref ulid), 0) = (byte)((CharToBase32[base32[0]] << 5) | CharToBase32[base32[1]]);
        Unsafe.Add(ref Unsafe.As<Ulid, byte>(ref ulid), 1) = (byte)((CharToBase32[base32[2]] << 3) | (CharToBase32[base32[3]] >> 2));
        Unsafe.Add(ref Unsafe.As<Ulid, byte>(ref ulid), 2) = (byte)((CharToBase32[base32[3]] << 6) | (CharToBase32[base32[4]] << 1) | (CharToBase32[base32[5]] >> 4));
        Unsafe.Add(ref Unsafe.As<Ulid, byte>(ref ulid), 3) = (byte)((CharToBase32[base32[5]] << 4) | (CharToBase32[base32[6]] >> 1));
        Unsafe.Add(ref Unsafe.As<Ulid, byte>(ref ulid), 4) = (byte)((CharToBase32[base32[6]] << 7) | (CharToBase32[base32[7]] << 2) | (CharToBase32[base32[8]] >> 3));
        Unsafe.Add(ref Unsafe.As<Ulid, byte>(ref ulid), 5) = (byte)((CharToBase32[base32[8]] << 5) | CharToBase32[base32[9]]);

        Unsafe.Add(ref Unsafe.As<Ulid, byte>(ref ulid), 6) = (byte)((CharToBase32[base32[10]] << 3) | (CharToBase32[base32[11]] >> 2));
        Unsafe.Add(ref Unsafe.As<Ulid, byte>(ref ulid), 7) = (byte)((CharToBase32[base32[11]] << 6) | (CharToBase32[base32[12]] << 1) | (CharToBase32[base32[13]] >> 4));
        Unsafe.Add(ref Unsafe.As<Ulid, byte>(ref ulid), 8) = (byte)((CharToBase32[base32[13]] << 4) | (CharToBase32[base32[14]] >> 1));
        Unsafe.Add(ref Unsafe.As<Ulid, byte>(ref ulid), 9) = (byte)((CharToBase32[base32[14]] << 7) | (CharToBase32[base32[15]] << 2) | (CharToBase32[base32[16]] >> 3));
        Unsafe.Add(ref Unsafe.As<Ulid, byte>(ref ulid), 10) = (byte)((CharToBase32[base32[16]] << 5) | CharToBase32[base32[17]]);
        Unsafe.Add(ref Unsafe.As<Ulid, byte>(ref ulid), 11) = (byte)((CharToBase32[base32[18]] << 3) | CharToBase32[base32[19]] >> 2);
        Unsafe.Add(ref Unsafe.As<Ulid, byte>(ref ulid), 12) = (byte)((CharToBase32[base32[19]] << 6) | (CharToBase32[base32[20]] << 1) | (CharToBase32[base32[21]] >> 4));
        Unsafe.Add(ref Unsafe.As<Ulid, byte>(ref ulid), 13) = (byte)((CharToBase32[base32[21]] << 4) | (CharToBase32[base32[22]] >> 1));
        Unsafe.Add(ref Unsafe.As<Ulid, byte>(ref ulid), 14) = (byte)((CharToBase32[base32[22]] << 7) | (CharToBase32[base32[23]] << 2) | (CharToBase32[base32[24]] >> 3));

        return ulid;
    }

    // Convert
    public byte[] ToByteArray()
    {
        var bytes = new byte[16];
        Unsafe.WriteUnaligned(ref bytes[0], this);
        return bytes;
    }

    public bool TryWriteBytes(Span<byte> destination)
    {
        if (destination.Length < 16)
        {
            return false;
        }

        Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(destination), this);
        return true;
    }

    public string ToBase64(Base64FormattingOptions options = Base64FormattingOptions.None)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16);
        try
        {
            TryWriteBytes(buffer);
            return Convert.ToBase64String(buffer, 0, 16, options);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public bool TryWriteStringify(Span<byte> span)
    {
        if (span.Length < 26)
        {
            return false;
        }

        span[25] = Base32Bytes[_randomness9 & 31]; // eliminate bounds-check of span

        // timestamp
        span[0] = Base32Bytes[(_timestamp0 & 224) >> 5];
        span[1] = Base32Bytes[_timestamp0 & 31];
        span[2] = Base32Bytes[(_timestamp1 & 248) >> 3];
        span[3] = Base32Bytes[((_timestamp1 & 7) << 2) | ((_timestamp2 & 192) >> 6)];
        span[4] = Base32Bytes[(_timestamp2 & 62) >> 1];
        span[5] = Base32Bytes[((_timestamp2 & 1) << 4) | ((_timestamp3 & 240) >> 4)];
        span[6] = Base32Bytes[((_timestamp3 & 15) << 1) | ((_timestamp4 & 128) >> 7)];
        span[7] = Base32Bytes[(_timestamp4 & 124) >> 2];
        span[8] = Base32Bytes[((_timestamp4 & 3) << 3) | ((_timestamp5 & 224) >> 5)];
        span[9] = Base32Bytes[_timestamp5 & 31];

        // randomness
        span[10] = Base32Bytes[(_randomness0 & 248) >> 3];
        span[11] = Base32Bytes[((_randomness0 & 7) << 2) | ((_randomness1 & 192) >> 6)];
        span[12] = Base32Bytes[(_randomness1 & 62) >> 1];
        span[13] = Base32Bytes[((_randomness1 & 1) << 4) | ((_randomness2 & 240) >> 4)];
        span[14] = Base32Bytes[((_randomness2 & 15) << 1) | ((_randomness3 & 128) >> 7)];
        span[15] = Base32Bytes[(_randomness3 & 124) >> 2];
        span[16] = Base32Bytes[((_randomness3 & 3) << 3) | ((_randomness4 & 224) >> 5)];
        span[17] = Base32Bytes[_randomness4 & 31];
        span[18] = Base32Bytes[(_randomness5 & 248) >> 3];
        span[19] = Base32Bytes[((_randomness5 & 7) << 2) | ((_randomness6 & 192) >> 6)];
        span[20] = Base32Bytes[(_randomness6 & 62) >> 1];
        span[21] = Base32Bytes[((_randomness6 & 1) << 4) | ((_randomness7 & 240) >> 4)];
        span[22] = Base32Bytes[((_randomness7 & 15) << 1) | ((_randomness8 & 128) >> 7)];
        span[23] = Base32Bytes[(_randomness8 & 124) >> 2];
        span[24] = Base32Bytes[((_randomness8 & 3) << 3) | ((_randomness9 & 224) >> 5)];

        return true;
    }

    public bool TryWriteStringify(Span<char> span)
    {
        if (span.Length < 26)
        {
            return false;
        }

        span[25] = Base32Text[_randomness9 & 31]; // eliminate bounds-check of span

        // timestamp
        span[0] = Base32Text[(_timestamp0 & 224) >> 5];
        span[1] = Base32Text[_timestamp0 & 31];
        span[2] = Base32Text[(_timestamp1 & 248) >> 3];
        span[3] = Base32Text[((_timestamp1 & 7) << 2) | ((_timestamp2 & 192) >> 6)];
        span[4] = Base32Text[(_timestamp2 & 62) >> 1];
        span[5] = Base32Text[((_timestamp2 & 1) << 4) | ((_timestamp3 & 240) >> 4)];
        span[6] = Base32Text[((_timestamp3 & 15) << 1) | ((_timestamp4 & 128) >> 7)];
        span[7] = Base32Text[(_timestamp4 & 124) >> 2];
        span[8] = Base32Text[((_timestamp4 & 3) << 3) | ((_timestamp5 & 224) >> 5)];
        span[9] = Base32Text[_timestamp5 & 31];

        // randomness
        span[10] = Base32Text[(_randomness0 & 248) >> 3];
        span[11] = Base32Text[((_randomness0 & 7) << 2) | ((_randomness1 & 192) >> 6)];
        span[12] = Base32Text[(_randomness1 & 62) >> 1];
        span[13] = Base32Text[((_randomness1 & 1) << 4) | ((_randomness2 & 240) >> 4)];
        span[14] = Base32Text[((_randomness2 & 15) << 1) | ((_randomness3 & 128) >> 7)];
        span[15] = Base32Text[(_randomness3 & 124) >> 2];
        span[16] = Base32Text[((_randomness3 & 3) << 3) | ((_randomness4 & 224) >> 5)];
        span[17] = Base32Text[_randomness4 & 31];
        span[18] = Base32Text[(_randomness5 & 248) >> 3];
        span[19] = Base32Text[((_randomness5 & 7) << 2) | ((_randomness6 & 192) >> 6)];
        span[20] = Base32Text[(_randomness6 & 62) >> 1];
        span[21] = Base32Text[((_randomness6 & 1) << 4) | ((_randomness7 & 240) >> 4)];
        span[22] = Base32Text[((_randomness7 & 15) << 1) | ((_randomness8 & 128) >> 7)];
        span[23] = Base32Text[(_randomness8 & 124) >> 2];
        span[24] = Base32Text[((_randomness8 & 3) << 3) | ((_randomness9 & 224) >> 5)];

        return true;
    }

    public override string ToString()
    {
        return string.Create<Ulid>(26, this, (span, state) =>
        {
            state.TryWriteStringify(span);
        });
    }

    //
    //ISpanFormattable
    //
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider provider)
    {
        if (TryWriteStringify(destination))
        {
            charsWritten = 26;
            return true;
        }
        else
        {
            charsWritten = 0;
            return false;
        }
    }

    public string ToString(string format, IFormatProvider formatProvider) => ToString();

    //
    // IParsable
    //
    /// <inheritdoc cref="IParsable{TSelf}.Parse(string, IFormatProvider?)" />
    public static Ulid Parse(string s, IFormatProvider provider) => Parse(s);

    /// <inheritdoc cref="IParsable{TSelf}.TryParse(string?, IFormatProvider?, out TSelf)" />
    public static bool TryParse([NotNullWhen(true)] string s, IFormatProvider provider, out Ulid result) => TryParse(s, out result);

    //
    // ISpanParsable
    //
    /// <inheritdoc cref="ISpanParsable{TSelf}.Parse(ReadOnlySpan{char}, IFormatProvider?)" />
    public static Ulid Parse(ReadOnlySpan<char> s, IFormatProvider provider) => Parse(s);

    /// <inheritdoc cref="ISpanParsable{TSelf}.TryParse(ReadOnlySpan{char}, IFormatProvider?, out TSelf)" />
    public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider provider, out Ulid result) => TryParse(s, out result);

    //
    // IUtf8SpanFormattable
    //
    public bool TryFormat(Span<byte> destination, out int bytesWritten, ReadOnlySpan<char> format, IFormatProvider provider)
    {
        if (TryWriteStringify(destination))
        {
            bytesWritten = 26;
            return true;
        }
        bytesWritten = 0;
        return false;
    }

    // Comparable/Equatable

    public override int GetHashCode()
    {
        ref int rA = ref Unsafe.As<Ulid, int>(ref Unsafe.AsRef(in this));
        return rA ^ Unsafe.Add(ref rA, 1) ^ Unsafe.Add(ref rA, 2) ^ Unsafe.Add(ref rA, 3);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool EqualsCore(in Ulid left, in Ulid right)
    {
        if (Vector128.IsHardwareAccelerated)
        {
            return Unsafe.As<Ulid, Vector128<byte>>(ref Unsafe.AsRef(in left)) == Unsafe.As<Ulid, Vector128<byte>>(ref Unsafe.AsRef(in right));
        }
        if (Sse2.IsSupported)
        {
            var vA = Unsafe.As<Ulid, Vector128<byte>>(ref Unsafe.AsRef(in left));
            var vB = Unsafe.As<Ulid, Vector128<byte>>(ref Unsafe.AsRef(in right));
            var cmp = Sse2.CompareEqual(vA, vB);
            return Sse2.MoveMask(cmp) == 0xFFFF;
        }

        ref var rA = ref Unsafe.As<Ulid, long>(ref Unsafe.AsRef(in left));
        ref var rB = ref Unsafe.As<Ulid, long>(ref Unsafe.AsRef(in right));

        // Compare each element
        return rA == rB && Unsafe.Add(ref rA, 1) == Unsafe.Add(ref rB, 1);
    }

    public bool Equals(Ulid other) => EqualsCore(this, other);

    public override bool Equals(object obj) => (obj is Ulid other) && EqualsCore(this, other);

    public static bool operator ==(Ulid a, Ulid b) => EqualsCore(a, b);

    public static bool operator !=(Ulid a, Ulid b) => !EqualsCore(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetResult(byte me, byte them) => me < them ? -1 : 1;

    public int CompareTo(Ulid other)
    {
        if (_timestamp0 != other._timestamp0)
        {
            return GetResult(_timestamp0, other._timestamp0);
        }
        if (_timestamp1 != other._timestamp1)
        {
            return GetResult(_timestamp1, other._timestamp1);
        }
        if (_timestamp2 != other._timestamp2)
        {
            return GetResult(_timestamp2, other._timestamp2);
        }
        if (_timestamp3 != other._timestamp3)
        {
            return GetResult(_timestamp3, other._timestamp3);
        }
        if (_timestamp4 != other._timestamp4)
        {
            return GetResult(_timestamp4, other._timestamp4);
        }
        if (_timestamp5 != other._timestamp5)
        {
            return GetResult(_timestamp5, other._timestamp5);
        }
        if (_randomness0 != other._randomness0)
        {
            return GetResult(_randomness0, other._randomness0);
        }
        if (_randomness1 != other._randomness1)
        {
            return GetResult(_randomness1, other._randomness1);
        }
        if (_randomness2 != other._randomness2)
        {
            return GetResult(_randomness2, other._randomness2);
        }
        if (_randomness3 != other._randomness3)
        {
            return GetResult(_randomness3, other._randomness3);
        }
        if (_randomness4 != other._randomness4)
        {
            return GetResult(_randomness4, other._randomness4);
        }
        if (_randomness5 != other._randomness5)
        {
            return GetResult(_randomness5, other._randomness5);
        }
        if (_randomness6 != other._randomness6)
        {
            return GetResult(_randomness6, other._randomness6);
        }
        if (_randomness7 != other._randomness7)
        {
            return GetResult(_randomness7, other._randomness7);
        }
        if (_randomness8 != other._randomness8)
        {
            return GetResult(_randomness8, other._randomness8);
        }
        if (_randomness9 != other._randomness9)
        {
            return GetResult(_randomness9, other._randomness9);
        }

        return 0;
    }

    public int CompareTo(object value)
    {
        if (value == null)
        {
            return 1;
        }

        if (value is Ulid ulid)
        {
            return this.CompareTo(ulid);
        }
        throw new ArgumentException("Object must be of type ULID.", nameof(value));
    }

    public static explicit operator Guid(Ulid _this)
    {
        return _this.ToGuid();
    }

    /// <summary>
    /// Convert this <c>Ulid</c> value to a <c>Guid</c> value with the same comparability.
    /// </summary>
    /// <remarks>
    /// The byte arrangement between Ulid and Guid is not preserved.
    /// </remarks>
    /// <returns>The converted <c>Guid</c> value</returns>
    public Guid ToGuid()
    {
        if (IsVector128Supported && BitConverter.IsLittleEndian)
        {
            var vector = Unsafe.As<Ulid, Vector128<byte>>(ref Unsafe.AsRef(in this));
            var shuffled = Shuffle(vector, Vector128.Create((byte)3, 2, 1, 0, 5, 4, 7, 6, 8, 9, 10, 11, 12, 13, 14, 15));

            return Unsafe.As<Vector128<byte>, Guid>(ref shuffled);
        }

        Span<byte> buf = stackalloc byte[16];
        
        if (BitConverter.IsLittleEndian)
        {
            // |A|B|C|D|E|F|G|H|I|J|K|L|M|N|O|P|
            // |D|C|B|A|...
            //      ...|F|E|H|G|...
            //              ...|I|J|K|L|M|N|O|P|
            ref var ptr = ref Unsafe.As<Ulid, uint>(ref Unsafe.AsRef(in this));
            var lower = BinaryPrimitives.ReverseEndianness(ptr);
            MemoryMarshal.Write(buf, ref lower);

            ptr = ref Unsafe.Add(ref ptr, 1);
            var upper = ((ptr & 0x00_FF_00_FF) << 8) | ((ptr & 0xFF_00_FF_00) >> 8);
            MemoryMarshal.Write(buf.Slice(4), ref upper);

            ref var upperBytes = ref Unsafe.As<uint, ulong>(ref Unsafe.Add(ref ptr, 1));
            MemoryMarshal.Write(buf.Slice(8), ref upperBytes);
        }
        else
        {
            MemoryMarshal.Write(buf, ref Unsafe.AsRef(in this));
        }

        return MemoryMarshal.Read<Guid>(buf);
    }

    private static bool IsVector128Supported
    {
        get
        {
            return Vector128.IsHardwareAccelerated;

        }
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Shuffle(Vector128<byte> value, Vector128<byte> mask)
    {
        Debug.Assert(BitConverter.IsLittleEndian);
        Debug.Assert(IsVector128Supported);

        if (Vector128.IsHardwareAccelerated)
        {
            return Vector128.Shuffle(value, mask);
        }
        if (Ssse3.IsSupported)
        {
            return Ssse3.Shuffle(value, mask);
        }
        throw new NotImplementedException();
    }

    #region Partials 
    partial class UlidTypeConverter : TypeConverter
    {
        private static readonly Type StringType = typeof(string);
        private static readonly Type GuidType = typeof(Guid);

        public override bool CanConvertFrom(ITypeDescriptorContext context, Type sourceType)
        {
            if (sourceType == StringType || sourceType == GuidType)
            {
                return true;
            }

            return base.CanConvertFrom(context, sourceType);
        }

        public override bool CanConvertTo(ITypeDescriptorContext context, Type destinationType)
        {
            if (destinationType == StringType || destinationType == GuidType)
            {
                return true;
            }

            return base.CanConvertTo(context, destinationType);
        }

        public override object ConvertFrom(ITypeDescriptorContext context,
            CultureInfo culture, object value)
        {
            switch (value)
            {
                case Guid g:
                    return new Ulid(g);
                case string stringValue:
                    return Ulid.Parse(stringValue);
            }

            return base.ConvertFrom(context, culture, value);
        }

        public override object ConvertTo(
            ITypeDescriptorContext context,
            CultureInfo culture,
            object value,
            Type destinationType)
        {
            if (value is Ulid ulid)
            {
                if (destinationType == StringType)
                {
                    return ulid.ToString();
                }

                if (destinationType == GuidType)
                {
                    return ulid.ToGuid();
                }
            }

            return base.ConvertTo(context, culture, value, destinationType);
        }
    }
    #endregion
}