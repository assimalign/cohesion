using System;
using System.Buffers.Binary;

namespace Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

/// <summary>
/// Reads the journal records a write to a journal file carries, off the bytes on the wire, so a
/// fault-injecting medium can fail the write that carries a given record (#1252). Since #1252 an
/// append writes nothing: the append buffer drains whole frames, in one write, at a commit, a
/// reader, the write-ahead gate, a checkpoint, a full buffer and a close, so the write that
/// loses a record is the drain that carries it, wherever that happens.
/// </summary>
/// <remarks>
/// The layout is the journal's frame (Storage DESIGN, "The journal"):
/// <c>[int32 bodyLength][int32 magic][body][uint32 CRC-32C(body)]</c>, little-endian, the body
/// starting <c>[byte version][long lsn][long transactionSequence][byte type]</c>. A write that does
/// not start with a whole frame is not a drain and carries no record.
/// </remarks>
public static class JournalFrames
{
    private const int PrefixSize = sizeof(int) + sizeof(int);
    private const int TypeOffset = 1 + sizeof(long) + sizeof(long);
    private const int ChecksumSize = sizeof(uint);
    private const int Magic = 0x324C4157; // 'WAL2'

    /// <summary>
    /// Gets whether <paramref name="written"/>, the bytes of one write to a journal file, carries a
    /// record of <paramref name="type"/>.
    /// </summary>
    /// <param name="written">The bytes of the write.</param>
    /// <param name="type">The record type to look for.</param>
    /// <returns>True when a whole frame in the write is a record of that type.</returns>
    public static bool Carries(ReadOnlySpan<byte> written, JournalRecordType type)
    {
        int offset = 0;
        while (written.Length - offset >= PrefixSize + TypeOffset + 1)
        {
            int bodyLength = BinaryPrimitives.ReadInt32LittleEndian(written[offset..]);
            int magic = BinaryPrimitives.ReadInt32LittleEndian(written[(offset + sizeof(int))..]);
            if (magic != Magic || bodyLength <= TypeOffset || bodyLength > written.Length - offset - PrefixSize - ChecksumSize)
            {
                return false;
            }

            if (written[offset + PrefixSize + TypeOffset] == (byte)type)
            {
                return true;
            }

            offset += PrefixSize + bodyLength + ChecksumSize;
        }

        return false;
    }
}
