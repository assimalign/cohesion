using System;
using System.Buffers.Binary;
using System.IO;
using Assimalign.Cohesion.Database.Sql.Storage;
using Assimalign.Cohesion.Database.Storage.Units;

namespace Assimalign.Cohesion.Database.Sql.Internal;

// Bounded creation metadata and a factory for Storage's recovered read-only view.
// Memory-image sources are borrowed without copying their page or journal bytes.
[global::System.Diagnostics.DebuggerDisplay("SqlStorageProbe")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlStorageProbe.DebugView))]
internal sealed record SqlStorageProbe(long Length, byte[] Data, byte[] Journal, byte[] Backup, bool HasSidecars = true)
{
    internal long JournalLength { get; init; } = Journal.LongLength;
    internal long BackupLength { get; init; } = Backup.LongLength;
    internal Func<SqlStorage>? OpenReadOnly { get; init; }

    internal bool IsPreHeader => Length < Page.Size && JournalLength == 0 && BackupLength == 0;
    // Storage.Open validates the header checksums before this predicate is used. Slot 1
    // must still be unwritten; an initial page count alone also describes later checkpoints.
    internal bool HasInitialHeader => Length >= Page.Size && Data.Length >= Page.Size
        && Data.AsSpan(SqlStorageHeaderLayout.Slot1Offset, SqlStorageHeaderLayout.SlotSize).IndexOfAnyExcept((byte)0) < 0
        && BinaryPrimitives.ReadInt64LittleEndian(Data.AsSpan(SqlStorageHeaderLayout.GenerationOffset)) == 1
        && BinaryPrimitives.ReadInt64LittleEndian(Data.AsSpan(SqlStorageHeaderLayout.LsnFloorOffset)) == 0
        && BinaryPrimitives.ReadInt64LittleEndian(Data.AsSpan(SqlStorageHeaderLayout.SequenceFloorOffset)) == 0
        && BinaryPrimitives.ReadInt64LittleEndian(Data.AsSpan(SqlStorageHeaderLayout.TotalPagesOffset)) == 2;

    internal bool IsSmall => Length <= 2L * Page.Size;

    internal SqlStorage Open()
        => OpenReadOnly?.Invoke() ?? SqlStorage.Open(ReadOnly(Data), ReadOnly(Journal), ReadOnly(Backup), checkpointOnOpen: false);

    private static MemoryStream ReadOnly(byte[] bytes) => new MemoryStream(bytes, writable: false);

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Sql.Internal.SqlStorageProbe _target;

        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlStorageProbe value)
        {
            _target = value;
        }

        public long Length => _target.Length;
        public byte[] Data => _target.Data;
        public byte[] Journal => _target.Journal;
        public byte[] Backup => _target.Backup;
        public bool HasSidecars => _target.HasSidecars;
    }
}
