using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Assimalign.Cohesion.Database.Storage.Internal;
using Assimalign.Cohesion.Database.Storage.Units;

namespace Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

internal static class RecoveryJournalScenario
{
    internal static byte[] Create(int seed)
    {
        var random = new Random(seed);
        var records = new List<JournalRecord>();
        long lsn = 0;
        void Add(long sequence, JournalRecordType type, long pageId = 0)
        {
            // File order, rather than LSN comparison, must decide the last applicable record.
            lsn = random.Next(5) == 0 ? random.Next(1, 12) : lsn + 1;
            records.Add(new JournalRecord(lsn, sequence, type, (PageId)pageId, default));
        }

        for (int pageId = 0; pageId <= 9; pageId++)
        {
            Add(20, JournalRecordType.FullPageImage, pageId);
        }
        Add(1, JournalRecordType.BeginTransaction);
        Add(1, JournalRecordType.PageDelta, 1);
        Add(2, JournalRecordType.FullPageImage, 1);
        Add(1, JournalRecordType.CommitTransaction);
        Add(2, JournalRecordType.RollbackTransaction);
        Add(3, JournalRecordType.PageDelta, 1);
        Add(3, JournalRecordType.CommittedPageImage, 2);
        Add(3, JournalRecordType.RollbackTransaction);
        Add(4, JournalRecordType.PageDelta, 1);
        Add(4, JournalRecordType.CommitTransaction);
        Add(3, JournalRecordType.BeginTransaction);
        Add(3, JournalRecordType.CommitTransaction);
        Add(0, JournalRecordType.Checkpoint);

        for (int iteration = 0; iteration < 160; iteration++)
        {
            long sequence = random.Next(1, 12);
            var type = random.Next(9) switch
            {
                0 => JournalRecordType.BeginTransaction,
                1 => JournalRecordType.CommitTransaction,
                2 => JournalRecordType.RollbackTransaction,
                3 => JournalRecordType.Checkpoint,
                4 => JournalRecordType.Operation,
                5 => JournalRecordType.FullPageImage,
                6 => JournalRecordType.CommittedPageImage,
                _ => JournalRecordType.PageDelta,
            };
            Add(sequence, type, StorageRecovery.IsPageRecord(type) ? random.Next(0, 10) : 0);
        }
        // This uncommitted FPI follows committed changes on the shared page in every seed.
        Add(21, JournalRecordType.FullPageImage, 1);

        var committed = records.Where(record => record.Type == JournalRecordType.CommitTransaction)
            .Select(record => record.TransactionSequence).ToHashSet();
        var pages = new Dictionary<long, byte[]>();
        using var encoded = new MemoryStream();
        foreach (var record in records)
        {
            byte[] payload = [];
            long pageId = (long)record.PageId;
            if (StorageRecovery.IsPageRecord(record.Type))
            {
                pages.TryGetValue(pageId, out var previous);
                previous ??= new byte[Page.Size];
                var next = (byte[])previous.Clone();
                next[Page.HeaderSize + random.Next(64)] = (byte)random.Next(256);
                BinaryPrimitives.WriteInt64LittleEndian(next, pageId);
                next[Page.TypeFieldOffset] = (byte)PageType.Data;
                payload = new byte[PageImageCodec.MaximumPayloadLength];
                int length;
                if (record.Type == JournalRecordType.FullPageImage)
                {
                    length = PageImageCodec.EncodeImage(next, payload);
                }
                else
                {
                    BinaryPrimitives.WriteInt64LittleEndian(payload,
                        BinaryPrimitives.ReadInt64LittleEndian(previous.AsSpan(Page.LsnFieldOffset)));
                    length = PageImageCodec.BaseLsnSize + (record.Type == JournalRecordType.PageDelta
                        ? PageImageCodec.EncodeRuns(previous, next, payload.AsSpan(PageImageCodec.BaseLsnSize))
                        : PageImageCodec.EncodeImage(next, payload.AsSpan(PageImageCodec.BaseLsnSize)));
                }
                Array.Resize(ref payload, length);
                if (record.Type == JournalRecordType.FullPageImage || committed.Contains(record.TransactionSequence))
                {
                    BinaryPrimitives.WriteInt64LittleEndian(next.AsSpan(Page.LsnFieldOffset), record.Lsn);
                    pages[pageId] = next;
                }
            }
            else if (record.Type == JournalRecordType.Checkpoint)
            {
                payload = new byte[2 * sizeof(long)];
                BinaryPrimitives.WriteInt64LittleEndian(payload, 12);
                BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(sizeof(long)), 20);
            }

            byte[] frame = new byte[38 + payload.Length];
            BinaryPrimitives.WriteInt32LittleEndian(frame, 26 + payload.Length);
            BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), 0x324C4157);
            var body = frame.AsSpan(8, 26 + payload.Length);
            body[0] = 4;
            BinaryPrimitives.WriteInt64LittleEndian(body[1..], record.Lsn);
            BinaryPrimitives.WriteInt64LittleEndian(body[9..], record.TransactionSequence);
            body[17] = (byte)record.Type;
            BinaryPrimitives.WriteInt64LittleEndian(body[18..], pageId);
            payload.CopyTo(body[26..]);
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(frame.Length - sizeof(uint)), Crc32C.Compute(body));
            encoded.Write(frame);
        }
        return encoded.ToArray();
    }
}
