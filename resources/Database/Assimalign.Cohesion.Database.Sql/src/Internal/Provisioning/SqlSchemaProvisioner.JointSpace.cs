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
    internal const long JointCopyCheckpointBytes = 8L * 1024 * 1024;

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
                long heapPages = 0, rows = 0;
                var trees = new Dictionary<string, (long Pages, int Height)>(StringComparer.OrdinalIgnoreCase);
                if (item.Previous is { } previous)
                {
                    var pages = _database.DataStorage.GetOwnerPages(previous.ObjectId);
                    heapPages = pages.Count;
                    foreach (var id in pages)
                    {
                        token.ThrowIfCancellationRequested();
                        using var handle = _database.DataStorage.PageManager.GetPage(id);
                        rows += new SlottedPage(handle.Page).SlotCount;
                    }
                    foreach (var registration in registrations.Where(value => value.ObjectId == previous.ObjectId))
                    {
                        var size = CountJointIndexPages(registration.RootPageId, previous.ObjectId, token);
                        trees.Add(registration.Definition.Name, size);
                        actualIndexPages += size.Pages;
                    }
                    long oldWidth = RowWidth(previous), newWidth = RowWidth(item.Shape, previous);
                    replacementPages += DivideUp(heapPages * Math.Max(oldWidth, newWidth), oldWidth);
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
                        long oldWidth = IndexWidth(item.Previous!, old), newWidth = IndexWidth(item.Shape, index, item.Previous);
                        pages = DivideUp(size.Pages * Math.Max(oldWidth, newWidth), oldWidth);
                        height = size.Height;
                        for (long growth = DivideUp(pages, size.Pages); growth > 1; growth = DivideUp(growth, 3)) { height++; }
                    }
                    else
                    {
                        // New trees: 50% leaf occupancy, 28 bytes per MVCC entry/slot,
                        // then internal levels at minimum fanout three (1024-byte keys).
                        long level = Math.Max(1, DivideUp(rows * IndexWidth(item.Shape, index, item.Previous), (SlottedPage.BodySize - 32) / 2));
                        pages = level;
                        while (level > 1) { level = DivideUp(level, 3); pages += level; height++; }
                    }
                    replacementPages += pages;
                    roots++;
                    // A split can touch the path, two children and sibling links per
                    // level. Existing height is measured; growth and new levels use
                    // minimum fanout three rather than a guessed heap/index multiplier.
                    touchesPerRow += 4L * height + 3;
                }
                maximumChunkTouches = Math.Max(maximumChunkTouches, Math.Min(64, rows) * touchesPerRow);
                totalTouches += rows * touchesPerRow;
                chunks += DivideUp(rows, 64);
            }
            long replacement = replacementPages * Page.Size;
            // RegisterTouch may log a full pre-image (including spill); commit logs
            // a delta/committed image. Each payload <= B+8; each frame is 38 bytes.
            // CheckpointReplacementCopy truncates at chunk boundaries. Only a final
            // chunk or the root-creation bracket can overshoot the 8 MiB threshold.
            long pageFrames = 2 * (Page.Size + 8 + 38);
            long journal = Math.Min((totalTouches + roots) * pageFrames + chunks * 4 * 38,
                JointCopyCheckpointBytes + Math.Max(maximumChunkTouches, roots) * pageFrames + 4 * 38);
            long subtotal = replacement + journal;
            // 25% for occupancy/width estimation, plus 16 MiB catalog publication
            // and reclamation reserve. Old owners are already allocated: no charge.
            long need = subtotal + DivideUp(subtotal, 4) + 16L * 1024 * 1024 + JointSpaceFloor(intent);
            JointSpaceNeed = need;
            JointSpaceIndexPages = actualIndexPages;
            CheckJointSpace(need);
        }
    }

    private static long DivideUp(long value, long divisor) => checked(value / divisor + (value % divisor == 0 ? 0 : 1));

    private static long RowWidth(SqlCatalogTable table, SqlCatalogTable? previous = null)
        => Math.Min(SlottedPage.MaxRecordSize + 4L, 29 + table.Columns.Sum(column => ReplacementColumnWidth(column, previous)));

    private static long IndexWidth(SqlCatalogTable table, SqlCatalogIndex index, SqlCatalogTable? previous = null)
        => 28 + Math.Min(1024, index.ColumnNames.Sum(name => ReplacementColumnWidth(table.FindColumn(name)!, previous)));

    private static long ReplacementColumnWidth(SqlCatalogColumn column, SqlCatalogTable? previous)
    {
        // The joint copier writes NULL for an added column without a default.
        // Its declared VARCHAR/BINARY bound does not become row data.
        if (previous is not null && previous.FindColumn(column.Name) is null && column.DefaultLiteral is null)
        { return 1; }
        // VARCHAR/BINARY bounds cannot enlarge an existing value. CHAR can repad,
        // while a changed domain can invoke an application cast: estimate its target.
        if (previous?.FindColumn(column.Name) is { } old && old.Type.Type == column.Type.Type &&
            column.Type.Type is DatabaseType.String or DatabaseType.Binary &&
            string.Equals(old.DomainName, column.DomainName, StringComparison.OrdinalIgnoreCase) && old.Collation == column.Collation)
        { return ColumnWidth(old.Type); }
        return ColumnWidth(column.Type);
    }

    private static long ColumnWidth(DatabaseTypeInfo type) => type.Type switch
    {
        DatabaseType.Boolean or DatabaseType.Int8 => 2,
        DatabaseType.Int16 => 3,
        DatabaseType.Int32 or DatabaseType.Float32 or DatabaseType.Date => 5,
        DatabaseType.Int64 or DatabaseType.Float64 or DatabaseType.Time or DatabaseType.TimeSpan => 9,
        DatabaseType.DateTime => 10,
        DatabaseType.DateTimeOffset => 11,
        DatabaseType.Decimal => 33, // tag, sign, exponent, at most 29 digits, terminator
        DatabaseType.Guid => 17,
        DatabaseType.Binary or DatabaseType.JsonBinary => Math.Min(SlottedPage.MaxRecordSize, 2L * (type.MaxLength ?? SlottedPage.MaxRecordSize) + 3),
        _ => Math.Min(SlottedPage.MaxRecordSize, 4L * (type.MaxLength ?? SlottedPage.MaxRecordSize) + 4),
    };

    // Storage's public owner snapshots contain only heap pages. Walk registered trees
    // locally on the worker rather than adding a public Storage bridge. B-tree format 2:
    // BTreeNode.cs documents the 32-byte header, directory, tiebreakers and child slots.
    // Work/retention is O(index pages), independent of all other owners. No row decode.
    private (long Pages, int Height) CountJointIndexPages(long root, ulong owner, CancellationToken token)
    {
        var pending = new Stack<(long Page, int Depth)>();
        var seen = new HashSet<long>();
        pending.Push((root, 1));
        int height = 0;
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
            if (body[3] == 1) { continue; }
            int count = BinaryPrimitives.ReadUInt16LittleEndian(body[4..]);
            if (32 + 2 * count > body.Length) { throw new StorageCorruptionException(handle.Id, ErrorMessages.JointIndexShapeInvalid); }
            pending.Push((BinaryPrimitives.ReadInt64LittleEndian(body[24..]), next.Depth + 1));
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
        return (seen.Count, height);
    }
}
