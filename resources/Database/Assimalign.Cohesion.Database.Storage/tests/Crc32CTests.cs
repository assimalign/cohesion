using System;
using System.Buffers.Binary;
using System.Text;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage.Internal;
using Assimalign.Cohesion.Database.Storage.Units;

namespace Assimalign.Cohesion.Database.Storage.Tests;

/// <summary>
/// CRC-32C through <see cref="System.Numerics.BitOperations.Crc32C(uint, ulong)"/> (#1251):
/// golden vectors, agreement with an independent bitwise implementation of the polynomial for
/// every length and alignment the storage feeds it, and the zero-padding path page checksums
/// use for their own field.
/// </summary>
public sealed class Crc32CTests
{
    [Theory(DisplayName = "Cohesion Test [Storage] - CRC-32C: golden vectors")]
    [InlineData("", 0x00000000u)]
    [InlineData("a", 0xC1D04330u)]
    [InlineData("abc", 0x364B3FB7u)]
    [InlineData("123456789", 0xE3069283u)]
    [InlineData("The quick brown fox jumps over the lazy dog", 0x22620404u)]
    public void Compute_KnownText_ShouldMatchTheCheckValue(string text, uint expected)
    {
        Crc32C.Compute(Encoding.ASCII.GetBytes(text)).ShouldBe(expected);
        Reference(Encoding.ASCII.GetBytes(text)).ShouldBe(expected);
    }

    /// <summary>
    /// The iSCSI test patterns of RFC 3720, appendix B.4: 32 bytes of zeros, of 0xFF, ascending
    /// and descending.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Storage] - CRC-32C: RFC 3720 B.4 vectors")]
    public void Compute_Rfc3720Patterns_ShouldMatch()
    {
        var zeros = new byte[32];
        var ones = new byte[32];
        ones.AsSpan().Fill(0xFF);
        var ascending = new byte[32];
        var descending = new byte[32];
        for (int i = 0; i < 32; i++)
        {
            ascending[i] = (byte)i;
            descending[i] = (byte)(31 - i);
        }

        Crc32C.Compute(zeros).ShouldBe(0x8A9136AAu);
        Crc32C.Compute(ones).ShouldBe(0x62A8AB43u);
        Crc32C.Compute(ascending).ShouldBe(0x46DD794Eu);
        Crc32C.Compute(descending).ShouldBe(0x113FDB5Cu);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - CRC-32C: agrees with a bitwise reference for every length up to 9,000 bytes and every alignment")]
    public void Compute_RandomContent_ShouldAgreeWithTheBitwiseReference()
    {
        var random = new Random(1251);
        var buffer = new byte[9_000 + 8];
        random.NextBytes(buffer);

        for (int length = 0; length <= 9_000; length += length < 64 ? 1 : 97)
        {
            for (int alignment = 0; alignment < 8; alignment++)
            {
                var data = buffer.AsSpan(alignment, length);
                Crc32C.Compute(data).ShouldBe(Reference(data), $"length {length}, alignment {alignment}");
            }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - CRC-32C: incremental segments and zero padding equal one pass")]
    public void Append_SegmentsAndZeros_ShouldEqualOnePass()
    {
        var random = new Random(42);
        var data = new byte[1_000];
        random.NextBytes(data);

        for (int split = 0; split <= data.Length; split += 37)
        {
            for (int zeros = 0; zeros <= 20; zeros++)
            {
                var whole = new byte[data.Length + zeros];
                data.AsSpan(0, split).CopyTo(whole);
                data.AsSpan(split).CopyTo(whole.AsSpan(split + zeros));

                uint state = Crc32C.Begin();
                state = Crc32C.Append(state, data.AsSpan(0, split));
                state = Crc32C.AppendZeros(state, zeros);
                state = Crc32C.Append(state, data.AsSpan(split));

                Crc32C.Finalize(state).ShouldBe(Reference(whole), $"split {split}, zeros {zeros}");
            }
        }
    }

    /// <summary>
    /// A page checksum folds the page with its own four checksum bytes taken as zero, through
    /// <see cref="Crc32C.AppendZeros"/>, so it never copies the page: it must equal the CRC of a
    /// copy whose checksum field is zeroed, whatever the field holds.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Storage] - CRC-32C: the page checksum's zero-padding path equals the CRC of the page with its field zeroed")]
    public void PageChecksum_Compute_ShouldEqualTheCrcOfThePageWithItsFieldZeroed()
    {
        var random = new Random(7);
        var page = new byte[Page.Size];

        for (int round = 0; round < 64; round++)
        {
            random.NextBytes(page);
            var zeroed = (byte[])page.Clone();
            zeroed.AsSpan(Page.ChecksumFieldOffset, sizeof(uint)).Clear();

            uint expected = Reference(zeroed);
            PageChecksum.Compute(page).ShouldBe(expected);
            Crc32C.Compute(zeroed).ShouldBe(expected);

            PageChecksum.Stamp(page);
            BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(Page.ChecksumFieldOffset)).ShouldBe(expected);
            Should.NotThrow(() => PageChecksum.Verify(page, (PageId)1L));

            page[round * 97 % Page.Size] ^= 0x10;
            if (round * 97 % Page.Size is < Page.ChecksumFieldOffset or >= Page.ChecksumFieldOffset + sizeof(uint))
            {
                Should.Throw<StorageCorruptionException>(() => PageChecksum.Verify(page, (PageId)1L));
            }
        }
    }

    /// <summary>
    /// The definition: reflected polynomial 0x82F63B78, one bit at a time, initial value and
    /// final XOR 0xFFFFFFFF. Independent of both the table-free hardware path and any table.
    /// </summary>
    private static uint Reference(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte value in data)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0x82F63B78u : crc >> 1;
            }
        }

        return ~crc;
    }
}
