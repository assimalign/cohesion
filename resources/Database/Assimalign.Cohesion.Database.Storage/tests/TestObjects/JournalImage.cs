using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

/// <summary>
/// Reads and edits the bytes of a journal the way a crash or a damaged medium would leave them:
/// the frames it holds, a copy cut at a byte, a copy without one frame (#1253).
/// </summary>
internal static class JournalImage
{
    private const int PrefixSize = sizeof(int) + sizeof(int);
    private const int Magic = 0x324C4157; // 'WAL2'

    /// <summary>
    /// The frames of a journal, in order, up to the first that does not frame.
    /// </summary>
    internal static List<Frame> Frames(byte[] journal)
    {
        var frames = new List<Frame>();
        int offset = 0;
        while (journal.Length - offset >= PrefixSize)
        {
            int body = BinaryPrimitives.ReadInt32LittleEndian(journal.AsSpan(offset));
            if (BinaryPrimitives.ReadInt32LittleEndian(journal.AsSpan(offset + sizeof(int))) != Magic
                || body < 26
                || journal.Length - offset - PrefixSize - sizeof(uint) < body)
            {
                break;
            }

            var span = journal.AsSpan(offset + PrefixSize, body);
            frames.Add(new Frame(
                offset,
                PrefixSize + body + sizeof(uint),
                (JournalRecordType)span[17],
                BinaryPrimitives.ReadInt64LittleEndian(span[1..]),
                BinaryPrimitives.ReadInt64LittleEndian(span[18..])));
            offset += PrefixSize + body + sizeof(uint);
        }

        return frames;
    }

    /// <summary>
    /// A copy of the journal without the frame at <paramref name="frame"/>: the frames around it
    /// still verify, so the gap is invisible to the read scan.
    /// </summary>
    internal static byte[] Without(byte[] journal, Frame frame)
        => [.. journal.AsSpan(0, frame.Offset), .. journal.AsSpan(frame.Offset + frame.Length)];

    /// <summary>
    /// The records a journal holds, decoded.
    /// </summary>
    internal static IReadOnlyList<JournalRecord> Records(byte[] journal)
        => new StreamJournal(new MemoryStream(journal)).ReadAll();

    /// <summary>
    /// The page records of a journal for one page, in order.
    /// </summary>
    internal static List<JournalRecord> PageRecords(byte[] journal, long pageId)
        => [.. Records(journal).Where(record => (long)record.PageId == pageId && Internal.StorageRecovery.IsPageRecord(record.Type))];

    /// <summary>
    /// A journal frame: where it lies, its record type, LSN and page.
    /// </summary>
    internal readonly record struct Frame(int Offset, int Length, JournalRecordType Type, long Lsn, long PageId);
}
