using System;
using System.Collections.Generic;
using System.Linq;

using Assimalign.Cohesion.Database.Sql.Storage;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Units;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Catalog;

public sealed partial class SqlCatalog
{
    // Owner pre-assignment: 15 = #1030, 16 = #1123, 17 = #1124.
    private const int identityCounterRecordKind = 16;
    private const int identityReservationSize = 32;
    private readonly Dictionary<ulong, IdentityCounter> _identityCounters = new();

    /// <summary>Allocates an identity value after durably reserving its block.</summary>
    /// <param name="table">The current published table description.</param>
    /// <returns>A value never previously allocated for this table identity.</returns>
    /// <exception cref="ArgumentNullException">The table is null.</exception>
    /// <exception cref="SqlCatalogException">The description is stale, has no identity column, or the counter is exhausted.</exception>
    /// <exception cref="StorageException">The reservation cannot be persisted.</exception>
    /// <exception cref="StorageOfflineException">A reservation commit was not confirmed; reopen before allocating again.</exception>
    /// <remarks>Allocation is independent of data transactions and does not advance the DDL generation.
    /// Rollback and restart can leave gaps. No caller receives a value before its reservation commit.</remarks>
    public long AllocateIdentity(SqlCatalogTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        lock (_sync)
        {
            ThrowIfOffline();
            if (!_tables.TryGetValue((table.Schema, table.Name), out var current) || !ReferenceEquals(current.Table, table))
            {
                throw new SqlCatalogException("CS04064", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.IdentityTableChanged, table.Schema, table.Name));
            }
            var column = table.Columns.SingleOrDefault(column => column.Identity is not null)
                ?? throw new SqlCatalogException("CS01015", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.IdentityColumnMissing, table.Schema, table.Name));
            var identity = column.Identity!;
            if (!_identityCounters.TryGetValue(table.ObjectId, out var counter))
            {
                counter = new IdentityCounter { Next = identity.Start };
                _identityCounters.Add(table.ObjectId, counter);
            }
            var (minimum, maximum) = IdentityBounds(column.Type.Type, identity.Increment);
            if (counter.Next < minimum || counter.Next > maximum)
            {
                string typeName = column.Type.Type switch
                {
                    DatabaseType.Int8 => "TINYINT",
                    DatabaseType.Int16 => "SMALLINT",
                    DatabaseType.Int32 => "INT",
                    _ => "BIGINT",
                };
                throw new SqlCatalogException("CS02002", string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    ErrorMessages.IdentityRangeExhausted, column.Name, typeName, identity.Increment > 0 ? maximum : minimum));
            }
            if (counter.Remaining == 0)
            {
                long first = checked((long)counter.Next);
                int count = 1;
                Int128 last = first;
                // Int128 makes overflow checks independent of SQL arithmetic policy, including Int64.MinValue increments.
                while (count < identityReservationSize && last + identity.Increment >= minimum && last + identity.Increment <= maximum)
                {
                    last += identity.Increment;
                    count++;
                }
                var writer = new DatabaseKeyWriter();
                writer.AppendInt32(identityCounterRecordKind).AppendInt64((long)table.ObjectId).AppendInt64(checked((long)last));
                using var transaction = _storage.BeginTransaction();
                var location = UpsertRecord(transaction, counter.Location, writer.ToArray());
                transaction.Commit();
                counter.Location = location;
                counter.Remaining = count;
            }
            long value = checked((long)counter.Next);
            counter.Next += identity.Increment;
            counter.Remaining--;
            return value;
        }
    }

    private static (long Minimum, long Maximum) IdentityBounds(DatabaseType type, long increment)
    {
        // PostgreSQL sequence.c:1462,1494: default bounds depend on generation direction.
        var (minimum, maximum) = type switch
        {
            DatabaseType.Int8 => (sbyte.MinValue, sbyte.MaxValue),
            DatabaseType.Int16 => (short.MinValue, short.MaxValue),
            DatabaseType.Int32 => (int.MinValue, int.MaxValue),
            DatabaseType.Int64 => (long.MinValue, long.MaxValue),
            _ => throw new SqlCatalogException("CS02003", ErrorMessages.IdentityIntegerRequired),
        };
        return increment > 0 ? (1, maximum) : (minimum, -1);
    }

    private void LoadIdentityCounter(ref DatabaseKeyReader reader, PageId pageId, int slotIndex)
    {
        ulong id = (ulong)reader.ReadInt64();
        long reserved = reader.ReadInt64();
        if (!reader.IsAtEnd || id == 0 || !_identityCounters.TryAdd(id, new IdentityCounter { Next = reserved, Location = (pageId, slotIndex) }))
        {
            throw new SqlCatalogException("CS06059", ErrorMessages.IdentityCounterMalformed);
        }
    }

    private void ValidateIdentityCounters(IReadOnlyDictionary<ulong, SqlCatalogTable> tablesById)
    {
        foreach (var (id, counter) in _identityCounters)
        {
            var column = (tablesById.TryGetValue(id, out var table) ? table : null)?.Columns.SingleOrDefault(column => column.Identity is not null)
                ?? throw new SqlCatalogException("CS06059", ErrorMessages.IdentityCounterOrphaned);
            var identity = column.Identity!;
            var (minimum, maximum) = IdentityBounds(column.Type.Type, identity.Increment);
            Int128 distance = counter.Next - identity.Start;
            if (counter.Next < minimum || counter.Next > maximum || distance % identity.Increment != 0 || distance / identity.Increment < 0)
            {
                throw new SqlCatalogException("CS06059", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.IdentityCounterInvalid, column.Name));
            }
            counter.Next += identity.Increment;
        }
    }

    private static void EnsureIdentityKept(SqlCatalogTable previous, SqlCatalogTable updated)
    {
        var oldColumn = previous.Columns.FirstOrDefault(column => column.Identity is not null);
        if (oldColumn is null) { return; }
        var column = updated.FindColumn(oldColumn.Name);
        if (column is null) { return; } // DROP COLUMN retires the separate counter.
        var oldIdentity = oldColumn.Identity!;
        if (column.Identity is not { } identity || column.Type.Type != oldColumn.Type.Type ||
            identity.IsAlways != oldIdentity.IsAlways || identity.Start != oldIdentity.Start || identity.Increment != oldIdentity.Increment)
        {
            throw new SqlCatalogException("CS03053", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.IdentityMetadataInvalid, column.Name));
        }
    }

    private void DeleteIdentityCounter(StorageTransaction transaction, ulong tableObjectId)
    {
        if (_identityCounters.TryGetValue(tableObjectId, out var counter) && counter.Location is { } location)
        {
            _storage.DeleteRow(transaction, location.PageId, location.SlotIndex);
        }
    }

    private sealed class IdentityCounter
    {
        internal Int128 Next { get; set; }
        internal int Remaining { get; set; }
        internal (PageId PageId, int SlotIndex)? Location { get; set; }
    }
}
