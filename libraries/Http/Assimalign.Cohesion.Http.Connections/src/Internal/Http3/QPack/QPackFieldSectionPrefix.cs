using System;
using System.IO;

using Assimalign.Cohesion.Http.Connections.Internal.Http3.Frames;

namespace Assimalign.Cohesion.Http.Connections.Internal.Http3.QPack;

/// <summary>
/// Decodes the QPACK Field Section Prefix (RFC 9204 §4.5.1): the encoded
/// Required Insert Count and the signed Delta Base, reconstructing the absolute
/// Required Insert Count (§4.5.1.1) and Base (§4.5.1.2) used to resolve dynamic
/// and post-base field-line references.
/// </summary>
internal readonly struct QPackFieldSectionPrefix
{
    private QPackFieldSectionPrefix(long requiredInsertCount, long baseIndex, int bodyOffset)
    {
        RequiredInsertCount = requiredInsertCount;
        Base = baseIndex;
        BodyOffset = bodyOffset;
    }

    /// <summary>Gets the reconstructed absolute Required Insert Count (0 when no dynamic entries are referenced).</summary>
    public long RequiredInsertCount { get; }

    /// <summary>Gets the Base against which relative and post-base indices are resolved.</summary>
    public long Base { get; }

    /// <summary>Gets the offset into the field section at which the field lines begin.</summary>
    public int BodyOffset { get; }

    /// <summary>
    /// Computes the number of dynamic-table entries that fit the advertised
    /// maximum capacity — <c>MaxEntries = floor(MaxTableCapacity / 32)</c>
    /// (RFC 9204 §3.2.2).
    /// </summary>
    /// <param name="maxTableCapacity">The advertised <c>QPACK_MAX_TABLE_CAPACITY</c>.</param>
    /// <returns>The maximum number of entries.</returns>
    public static long MaxEntries(long maxTableCapacity) => maxTableCapacity / 32;

    /// <summary>
    /// Parses the field section prefix.
    /// </summary>
    /// <param name="buffer">The encoded field section.</param>
    /// <param name="maxTableCapacity">The advertised maximum table capacity.</param>
    /// <param name="totalNumberOfInserts">The decoder's current insert count.</param>
    /// <returns>The parsed prefix.</returns>
    /// <exception cref="InvalidDataException">Thrown when the prefix is truncated.</exception>
    /// <exception cref="QPackException">
    /// Thrown (<c>QPACK_DECOMPRESSION_FAILED</c>) when the encoded Required Insert
    /// Count cannot be reconstructed.
    /// </exception>
    public static QPackFieldSectionPrefix Parse(ReadOnlySpan<byte> buffer, long maxTableCapacity, long totalNumberOfInserts)
    {
        int index = 0;

        long encodedInsertCount = QPackPrefixedInteger.Decode(buffer, ref index, 8);

        // Delta Base carries a sign bit (S) at bit 7 followed by a 7-bit prefix.
        bool signNegative = index < buffer.Length && (buffer[index] & 0x80) != 0;
        long deltaBase = QPackPrefixedInteger.Decode(buffer, ref index, 7);

        long requiredInsertCount = DecodeRequiredInsertCount(encodedInsertCount, MaxEntries(maxTableCapacity), totalNumberOfInserts);
        long baseIndex = signNegative
            ? requiredInsertCount - deltaBase - 1
            : requiredInsertCount + deltaBase;

        return new QPackFieldSectionPrefix(requiredInsertCount, baseIndex, index);
    }

    /// <summary>
    /// Reconstructs the absolute Required Insert Count from its encoded form
    /// (RFC 9204 §4.5.1.1).
    /// </summary>
    /// <param name="encodedInsertCount">The encoded Required Insert Count.</param>
    /// <param name="maxEntries">The maximum number of dynamic-table entries.</param>
    /// <param name="totalNumberOfInserts">The decoder's current insert count.</param>
    /// <returns>The absolute Required Insert Count.</returns>
    /// <exception cref="QPackException">
    /// Thrown (<c>QPACK_DECOMPRESSION_FAILED</c>) when the encoded value is invalid.
    /// </exception>
    public static long DecodeRequiredInsertCount(long encodedInsertCount, long maxEntries, long totalNumberOfInserts)
    {
        if (encodedInsertCount == 0)
        {
            return 0;
        }

        long fullRange = 2 * maxEntries;

        if (fullRange == 0 || encodedInsertCount > fullRange)
        {
            throw new QPackException(
                Http3ErrorCode.QPackDecompressionFailed,
                "The QPACK encoded Required Insert Count is out of range for the advertised table capacity.");
        }

        long maxValue = totalNumberOfInserts + maxEntries;
        long maxWrapped = maxValue / fullRange * fullRange;
        long requiredInsertCount = maxWrapped + encodedInsertCount - 1;

        if (requiredInsertCount > maxValue)
        {
            if (requiredInsertCount <= fullRange)
            {
                throw new QPackException(
                    Http3ErrorCode.QPackDecompressionFailed,
                    "The QPACK Required Insert Count could not be reconstructed.");
            }

            requiredInsertCount -= fullRange;
        }

        if (requiredInsertCount == 0)
        {
            throw new QPackException(
                Http3ErrorCode.QPackDecompressionFailed,
                "The QPACK Required Insert Count reconstructed to zero.");
        }

        return requiredInsertCount;
    }
}
