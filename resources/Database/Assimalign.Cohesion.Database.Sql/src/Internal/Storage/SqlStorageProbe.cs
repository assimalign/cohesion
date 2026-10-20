using System;
using System.Buffers.Binary;
using System.IO;
using Assimalign.Cohesion.Database.Sql.Storage;
using Assimalign.Cohesion.Database.Storage.Units;

namespace Assimalign.Cohesion.Database.Sql.Internal;

// A bounded read-only file image. Recovery, if needed for an emptiness decision, runs
// only on private copies, never on the files a refusal must preserve.
internal sealed record SqlStorageProbe(long Length, byte[] Data, byte[] Journal, byte[] Backup, bool HasSidecars = true)
{
    internal long JournalLength { get; init; } = Journal.LongLength;
    internal long BackupLength { get; init; } = Backup.LongLength;
    internal Func<SqlStorage>? OpenReadOnly { get; init; }

    internal bool IsPreHeader => Length < Page.Size && JournalLength == 0 && BackupLength == 0;
    // Storage.Open validates the header checksums before this predicate is used. Slot 1
    // must still be unwritten; an initial page count alone also describes later checkpoints.
    internal bool HasInitialHeader => Length >= Page.Size && Data.Length >= Page.Size
        && Data.AsSpan(4608, 3584).IndexOfAnyExcept((byte)0) < 0
        && BinaryPrimitives.ReadInt64LittleEndian(Data.AsSpan(520)) == 1
        && BinaryPrimitives.ReadInt64LittleEndian(Data.AsSpan(528)) == 0
        && BinaryPrimitives.ReadInt64LittleEndian(Data.AsSpan(536)) == 0
        && BinaryPrimitives.ReadInt64LittleEndian(Data.AsSpan(544)) == 2;

    internal bool IsSmall => Length <= 2L * Page.Size;

    internal SqlStorage Open()
        => OpenReadOnly?.Invoke() ?? SqlStorage.Open(Copy(Data), Copy(Journal), Copy(Backup), checkpointOnOpen: false);

    private static MemoryStream Copy(byte[] bytes)
    {
        var stream = new MemoryStream();
        stream.Write(bytes);
        stream.Position = 0;
        return stream;
    }
}
