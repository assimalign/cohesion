using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Units;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlSchemaProvisioner
{
    internal const long JointCopyCheckpointBytes = 16L * 1024 * 1024;

    // One new page per 3 KiB kind-9 chunk, plus image/delta frames for both
    // deleting the old intent and writing its replacement. Old pages are already
    // allocated; only their WAL is additional (38-byte frames, 8-byte base LSN).
    // Four pages cover directory/counter churn.
    // The extra MiB covers allocation/checkpoint headers and record encoding slack.
    // This work depends on declaration size, never stored table size.
    private static long JointSpaceFloor(SqlCatalogPendingMigration intent)
    {
        long bytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(intent, SqlCatalogJsonContext.Default.SqlCatalogPendingMigration));
        return checked(1024 * 1024 + (DivideUp(bytes, 3 * 1024) + 4) * (5 * Page.Size + 168));
    }

    private void CheckJointSpace(long need)
    {
        long available = _database.Engine.GetAvailableMigrationBytes(_database.Name);
        if (available >= need) { return; }
        string message = string.Format(System.Globalization.CultureInfo.InvariantCulture,
            ErrorMessages.JointSpaceInsufficient, need, available);
        throw CodedMigrationFailure(SqlProvisioningCodes.JointFailed, message);
    }

    private void AdmitJointSpace(SqlJointMigrationPlan plan, SqlCatalogPendingMigration intent, CancellationToken token)
    {
        JointSpaceCounting?.Invoke();
        long replacementPages = 0, actualIndexPages = 0, maximumChunkTouches = 0, totalTouches = 0, chunks = 1, roots = 0;
        var registrations = _database.IndexManager.ExportRegistrations();
        checked
        {
            foreach (var item in plan.Tables)
            {
                token.ThrowIfCancellationRequested();
                long heapPages = 0, rows = 0, recordBytes = 0;
                long[] columnBytes = item.Previous is null ? [] : new long[item.Previous.Columns.Count];
                long[] keyColumnBytes = new long[columnBytes.Length];
                long[] nonNullRows = new long[columnBytes.Length];
                var trees = new Dictionary<string, (long Pages, int Height, long Bytes)>(StringComparer.OrdinalIgnoreCase);
                if (item.Previous is { } previous)
                {
                    var missing = _database.Definitions.Get(previous, token).MissingValues;
                    var missingWidths = previous.Columns.Select((column, ordinal) =>
                    {
                        var writer = new DatabaseKeyWriter();
                        SqlRowCodec.AppendValue(writer, column.Type.Type, missing[ordinal] is SqlBoundConstant value ? value.Value : null);
                        return writer.ToArray().Length;
                    }).ToArray();
                    heapPages = _database.DataStorage.GetOwnerPages(previous.ObjectId).Count;
                    using var units = _database.DataStorage.GetUnitIterator(previous.ObjectId);
                    while (units.Next(out var unit))
                    {
                        token.ThrowIfCancellationRequested();
                        // The owner-scoped iterator confirms each private byte copy
                        // against pruning/rollback. No row value is materialized.
                        rows++;
                        recordBytes += unit.Data.Length + 4;
                        try { CountJointColumnBytes(unit.Data.Span, previous, columnBytes, keyColumnBytes, missingWidths, nonNullRows); }
                        catch (DatabaseTypeException defect)
                        { throw new StorageCorruptionException(unit.PageId, unit.SlotIndex, ErrorMessages.JointIndexShapeInvalid, defect); }
                    }
                    foreach (var registration in registrations.Where(value => value.ObjectId == previous.ObjectId))
                    {
                        var size = CountJointIndexPages(registration.RootPageId, previous.ObjectId, token);
                        trees.Add(registration.Definition.Name, size);
                        actualIndexPages += size.Pages;
                    }
                    long newBytes = 29 * rows;
                    for (int column = 0; column < item.Shape.Columns.Count; column++)
                    { newBytes += JointColumnBytes(item, column, columnBytes, nonNullRows, rows); }
                    replacementPages += recordBytes == 0 ? 0 : DivideUp(heapPages * Math.Max(recordBytes, newBytes), recordBytes);
                }
                // Empty/new tables allocate no heap until an application writes them.
                long touchesPerRow = 2;
                foreach (var index in item.Indexes)
                {
                    long pages;
                    int height = 1;
                    var old = item.Previous is null ? null : _catalog.GetIndexes(item.Previous.ObjectId)
                        .FirstOrDefault(value => value.Name.Equals(index.Name, StringComparison.OrdinalIgnoreCase) &&
                            value.ColumnNames.SequenceEqual(index.ColumnNames, StringComparer.OrdinalIgnoreCase));
                    if (old is not null && trees.TryGetValue(old.Name, out var size))
                    {
                        long newBytes = size.Bytes + index.ColumnNames.Sum(name =>
                        {
                            int ordinal = item.Shape.Columns.ToList().FindIndex(column => column.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                            int oldOrdinal = item.Previous!.Columns.ToList().FindIndex(column => column.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                            return JointColumnBytes(item, ordinal, keyColumnBytes, nonNullRows, rows, key: true) - keyColumnBytes[oldOrdinal];
                        });
                        pages = size.Bytes == 0 ? size.Pages : DivideUp(size.Pages * Math.Max(size.Bytes, newBytes), size.Bytes);
                        height = size.Height;
                        for (long growth = DivideUp(pages, size.Pages); growth > 1; growth = DivideUp(growth, 3)) { height++; }
                    }
                    else
                    {
                        // New trees: 50% leaf occupancy, 28 bytes per MVCC entry/slot,
                        // then internal levels at minimum fanout three (1024-byte keys).
                        long keyBytes = 28 * rows + index.ColumnNames.Sum(name => JointColumnBytes(item,
                            item.Shape.Columns.ToList().FindIndex(column => column.Name.Equals(name, StringComparison.OrdinalIgnoreCase)), keyColumnBytes, nonNullRows, rows, key: true));
                        long level = Math.Max(1, DivideUp(keyBytes, (SlottedPage.BodySize - 32) / 2));
                        pages = level;
                        while (level > 1) { level = DivideUp(level, 3); pages += level; height++; }
                    }
                    replacementPages += pages;
                    roots++;
                    // Tree pages and heights were measured without decoding keys.
                }
                item.CopyRows = rows;
                touchesPerRow = JointCopyTouches(1, rows, item.Indexes.Length);
                maximumChunkTouches = Math.Max(maximumChunkTouches, Math.Min(64, rows) * touchesPerRow);
                totalTouches += rows * touchesPerRow;
                chunks += DivideUp(rows, 64);
            }
            long replacement = replacementPages * Page.Size;
            JointSpaceReplacementBytes = replacement;
            // RegisterTouch may log a full pre-image (including spill); commit logs
            // a delta/committed image. Each payload <= B+8; each frame is 38 bytes.
            // CheckpointReplacementCopy truncates at chunk boundaries. Only a final
            // chunk or the root-creation bracket can overshoot the 16 MiB threshold.
            long pageFrames = 2 * (Page.Size + 8 + 38);
            long journal = Math.Min((totalTouches + roots) * pageFrames + chunks * 4 * 38,
                JointCopyCheckpointBytes + Math.Max(maximumChunkTouches, roots) * pageFrames + 4 * 38);
            long subtotal = replacement + journal;
            // 25% for occupancy/width estimation, plus 16 MiB catalog publication
            // and reclamation reserve. Old owners are already allocated: no charge.
            JointSpaceDiscardBytes = JointDiscardReserve(plan, intent);
            long need = subtotal + DivideUp(subtotal, 4) + 16L * 1024 * 1024 + JointSpaceDiscardBytes;
            JointSpaceNeed = need;
            JointSpaceIndexPages = actualIndexPages;
            CheckJointSpace(need);
        }
    }

    internal static long JointCopyTouches(long count, long totalRows, int indexes)
    {
        // Even maximum-size keys split at fanout >= 3. All source versions count;
        // deletion/pruning can only reduce the prepared tree's maximum height.
        int height = 1;
        for (long entries = totalRows; entries > 3; entries = DivideUp(entries, 3)) { height++; }
        return checked(count * (2 + indexes * (4L * height + 3)));
    }

    internal static long JointCopyAllocations(long count, long totalRows, int indexes)
    {
        int height = 1;
        for (long entries = totalRows; entries > 3; entries = DivideUp(entries, 3)) { height++; }
        // One new heap page per row; a split allocates one sibling per level,
        // except root growth which allocates two. Two per level bounds both.
        return checked(count * (1 + indexes * 2L * height));
    }

    internal static long JointBracketBytes(long touches, long allocations)
        => checked(allocations * Page.Size + touches * 2 * (Page.Size + 46) + 152);

    private static long DivideUp(long value, long divisor) => checked(value / divisor + (value % divisor == 0 ? 0 : 1));

    // Kind 14 already exists for every prepared owner. Cover kind-9 deletion,
    // directory removal per tree/owner and their WAL, without a heap walk.
    private static long JointDiscardReserve(SqlJointMigrationPlan plan, SqlCatalogPendingMigration intent)
        => checked(JointSpaceFloor(intent) +
            (4 + 2L * plan.Tables.Length + 2L * plan.Tables.Sum(table => table.Indexes.Length)) *
            (Page.Size + 2 * (Page.Size + 46)));

    private static long JointColumnBytes(SqlJointMigrationTable item, int ordinal, long[] measured, long[] nonNullRows, long rows, bool key = false)
    {
        var column = item.Shape.Columns[ordinal];
        var old = item.Previous?.FindColumn(column.Name);
        if (old is null)
        {
            if (item.Values[ordinal] is SqlBoundConstant constant)
            {
                var writer = new DatabaseKeyWriter();
                if (key) { SqlRowCodec.AppendKeyValue(writer, column.Type.Type, constant.Value, column.Collation ?? Collation.Binary); }
                else { SqlRowCodec.AppendValue(writer, column.Type.Type, constant.Value); }
                return rows * writer.ToArray().Length;
            }
            return rows; // NULL, or no knowable bound on an expression default.
        }
        int oldOrdinal = item.Previous!.Columns.ToList().IndexOf(old);
        long actual = measured[oldOrdinal];
        long nonNull = nonNullRows[oldOrdinal], nulls = rows - nonNull;
        if (item.Values[ordinal] is not SqlBoundCast conversion) { return actual; }
        if (conversion.Casts[(int)old.Type.Type] is { } cast && !SqlStandardCasts.Contains(cast))
        {
            // NULL never invokes a cast. Opaque output stays 1x, with the known
            // minimum CHAR padding included for potentially non-NULL results.
            return column.Type.Type == DatabaseType.Character && !key
                ? Math.Max(actual, checked(nulls + nonNull * ((column.Type.MaxLength ?? 1) + 4L))) : actual;
        }
        if (column.Type.Type == DatabaseType.Character)
        {
            long width = column.Type.MaxLength ?? 1;
            long padding = Math.Max(0, width -
                (old.Type.Type == DatabaseType.Character ? old.Type.MaxLength ?? 1 : 0));
            if (old.Type.Type is DatabaseType.String or DatabaseType.Character)
            { return key ? actual : checked(actual + nonNull * padding); }
            int rendered = RenderedWidth(old.Type.Type);
            if (rendered != 0 || old.Type.Type == DatabaseType.Binary)
            {
                // Successful built-in scalar/hex output is ASCII and pads to n;
                // longer non-space output refuses assignment before any write.
                long length = key && rendered != 0 ? Math.Min(width, rendered) : width;
                return checked(nulls + nonNull * (length + 4));
            }
            return key ? actual : Math.Max(actual, checked(nulls + nonNull * (width + 4)));
        }
        if (column.Type.Type == DatabaseType.String)
        {
            int rendered = RenderedWidth(old.Type.Type);
            if (old.Type.Type == DatabaseType.Binary) { return checked(nulls + 2 * (actual - nulls) + 4 * nonNull); }
            return rendered == 0 ? actual : checked(nulls + nonNull * (rendered + 4));
        }
        int fixedWidth = column.Type.Type == DatabaseType.Decimal ? 33 : FixedColumnWidth(column.Type.Type);
        return fixedWidth == 0 ? actual : checked(nulls + nonNull * fixedWidth);
    }

    private static int RenderedWidth(DatabaseType type) => type switch
    {
        DatabaseType.Boolean => 5, DatabaseType.Int8 => 4, DatabaseType.Int16 => 6,
        DatabaseType.Int32 => 11, DatabaseType.Int64 => 20, DatabaseType.Decimal => 31,
        DatabaseType.Float32 => 16, DatabaseType.Float64 => 25, DatabaseType.Date => 10,
        DatabaseType.Time => 16, DatabaseType.DateTime => 27, DatabaseType.DateTimeOffset => 33,
        DatabaseType.TimeSpan => 32, DatabaseType.Guid => 36, _ => 0,
    };

    private static int FixedColumnWidth(DatabaseType type) => type switch
    {
        DatabaseType.Null => 1,
        DatabaseType.Boolean or DatabaseType.Int8 => 2, DatabaseType.Int16 => 3,
        DatabaseType.Int32 or DatabaseType.Float32 or DatabaseType.Date => 5,
        DatabaseType.Int64 or DatabaseType.Float64 or DatabaseType.Time or DatabaseType.TimeSpan => 9,
        DatabaseType.DateTime => 10, DatabaseType.DateTimeOffset => 11, DatabaseType.Guid => 17,
        _ => 0,
    };

    // Walk self-delimiting component lengths, without materializing values. The
    // public reader validates each component; a local length walk avoids widening
    // its API just for this consumer. Row strings always use binary collation.
    private static void CountJointColumnBytes(ReadOnlySpan<byte> record, SqlCatalogTable table, long[] totals, long[] keyTotals, int[] missingWidths, long[] nonNullRows)
    {
        int offset = SqlRowCodec.StampHeaderSize + 9, physical = 0;
        if (offset > record.Length) { throw new DatabaseTypeException(ErrorMessages.JointIndexShapeInvalid); }
        for (int column = 0; column < table.Columns.Count; column++)
        {
            int target = table.GetPhysicalOrdinal(column);
            while (physical < target && offset < record.Length)
            { offset += JointComponentLength(record[offset..]); physical++; }
            if (offset == record.Length)
            {
                totals[column] += missingWidths[column];
                keyTotals[column] += missingWidths[column];
                if (missingWidths[column] > 1) { nonNullRows[column]++; }
                continue;
            }
            int length = JointComponentLength(record[offset..]);
            totals[column] += length;
            if (record[offset] != (byte)DatabaseType.Null) { nonNullRows[column]++; }
            int keyLength = length;
            if (table.Columns[column].Type.Type == DatabaseType.Character && length >= 4)
            {
                // CHAR key identity strips ASCII padding; row bytes retain it.
                while (keyLength > 4 && record[offset + keyLength - 3] == (byte)' ') { keyLength--; }
            }
            keyTotals[column] += keyLength;
            offset += length;
            physical++;
        }
    }

    private static int JointComponentLength(ReadOnlySpan<byte> source)
    {
        var reader = new DatabaseKeyReader(source);
        var type = reader.PeekType();
        reader.Skip(); // Validate geometry/terminators before the length-only walk.
        int fixedWidth = FixedColumnWidth(type);
        if (fixedWidth != 0) { return fixedWidth; }
        if (type == DatabaseType.Decimal)
        {
            if (source[1] == 1) { return 2; }
            return 4 + source[3..].IndexOf(source[1] == 0 ? (byte)255 : (byte)0);
        }
        int position = type == DatabaseType.String ? 2 : 1;
        while (true)
        {
            position += source[position..].IndexOf((byte)0) + 1;
            if (source[position++] == 0) { break; }
        }
        if (type == DatabaseType.String && !Collation.FromId(source[1]).IsIndexBacked)
        { position += 4 + BinaryPrimitives.ReadInt32BigEndian(source[position..]); }
        return position;
    }

    // Storage's public owner snapshots contain only heap pages. Walk registered trees
    // locally on the worker rather than adding a public Storage bridge. B-tree format 2:
    // BTreeNode.cs documents the 32-byte header, directory, tiebreakers and child slots.
    // Work/retention is O(index pages), independent of all other owners. No row decode.
    private (long Pages, int Height, long Bytes) CountJointIndexPages(long root, ulong owner, CancellationToken token)
    {
        var pending = new Stack<(long Page, int Depth)>();
        var seen = new HashSet<long>();
        pending.Push((root, 1));
        int height = 0;
        long leafBytes = 0;
        while (pending.TryPop(out var next))
        {
            token.ThrowIfCancellationRequested();
            if (next.Page <= 0 || next.Page >= _database.DataStorage.PageManager.PageCount || !seen.Add(next.Page))
            { throw new StorageCorruptionException((PageId)next.Page, ErrorMessages.JointIndexShapeInvalid); }
            using var handle = _database.DataStorage.PageManager.GetPage((PageId)next.Page);
            var body = handle.Page.AsBodySpan();
            if (handle.Page.Type != PageType.Index || handle.Page.OwnerId != owner ||
                BinaryPrimitives.ReadUInt16LittleEndian(body) != 0x5442 || body[2] != 2 || body[3] is not (1 or 2))
            { throw new StorageCorruptionException(handle.Id, ErrorMessages.JointIndexShapeInvalid); }
            height = Math.Max(height, next.Depth);
            int count = BinaryPrimitives.ReadUInt16LittleEndian(body[4..]);
            if (32 + 2 * count > body.Length) { throw new StorageCorruptionException(handle.Id, ErrorMessages.JointIndexShapeInvalid); }
            if (body[3] == 1)
            {
                // Pruning only shifts/clears the directory; its orphaned payload stays
                // until insertion/compaction. Table X excludes those rebuilds. Measure
                // the stable high-water payload, conservatively including that garbage,
                // instead of racing a shifted directory while reading key lengths.
                int start = BinaryPrimitives.ReadUInt16LittleEndian(body[6..]);
                if (start < 32 + 2 * count || start > body.Length)
                { throw new StorageCorruptionException(handle.Id, ErrorMessages.JointIndexShapeInvalid); }
                leafBytes += body.Length - start + 2 * count;
                continue;
            }
            if (body[3] == 2) { pending.Push((BinaryPrimitives.ReadInt64LittleEndian(body[24..]), next.Depth + 1)); }
            for (int i = 0; i < count; i++)
            {
                int offset = BinaryPrimitives.ReadUInt16LittleEndian(body[(32 + 2 * i)..]);
                if (offset < 32 + 2 * count || offset > body.Length - 2)
                { throw new StorageCorruptionException(handle.Id, ErrorMessages.JointIndexShapeInvalid); }
                int header = BinaryPrimitives.ReadUInt16LittleEndian(body[offset..]);
                int tie = header >> 14, key = header & 0x3fff;
                int child = offset + 2 + key + tie * 8;
                if (tie > 2 || key > 1024 || child > body.Length - 8)
                { throw new StorageCorruptionException(handle.Id, ErrorMessages.JointIndexShapeInvalid); }
                pending.Push((BinaryPrimitives.ReadInt64LittleEndian(body[child..]), next.Depth + 1));
            }
        }
        return (seen.Count, height, leafBytes);
    }
}
