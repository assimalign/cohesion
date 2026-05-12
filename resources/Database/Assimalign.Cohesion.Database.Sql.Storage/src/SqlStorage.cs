using System;

namespace Assimalign.Cohesion.Database.Sql.Storage;

using Assimalign.Cohesion.Database.Storage;

/// <summary>
/// Provides a row-oriented storage engine built on the shared page-based storage layer.
/// Rows are stored as variable-length byte records in slotted pages.
/// </summary>
/// <remarks>
/// Each <see cref="SqlStorage"/> instance manages three file assets: data (<c>.dat</c>),
/// journal (<c>.log</c>), and backup (<c>.bak</c>). Use the static factory methods
/// <see cref="Create(StorageStream, StorageStream, StorageStream, string)"/> and
/// <see cref="Open(StorageStream, StorageStream, StorageStream)"/> to create or open
/// storage instances.
/// </remarks>
/// <example>
/// <code>
/// // Create a new SQL storage backed by in-memory streams
/// using var data = StorageStream.FromInMemory();
/// using var journal = StorageStream.FromInMemory();
/// using var backup = StorageStream.FromInMemory();
/// using var storage = SqlStorage.Create(data, journal, backup, "users-table");
///
/// // Serialize row data (e.g., fixed-width columns: int32 Id + 64-byte Name)
/// byte[] row1 = new byte[68];
/// BitConverter.TryWriteBytes(row1, 1);
/// System.Text.Encoding.UTF8.GetBytes("Alice").CopyTo(row1.AsSpan(4));
///
/// var (page1, slot1) = storage.InsertRow(row1);
///
/// // Read a row back
/// ReadOnlyMemory&lt;byte&gt; rowData = storage.ReadRow(page1, slot1);
/// int id = BitConverter.ToInt32(rowData.Span);    // 1
///
/// // Flush changes to the underlying streams
/// storage.FlushChanges();
/// </code>
/// </example>
public sealed class SqlStorage : Assimalign.Cohesion.Database.Storage.Storage
{
    private SqlStorage(StorageStream data, StorageStream journal, StorageStream backup)
        : base(data, journal, backup) { }

    /// <inheritdoc />
    public override StorageModel Model => StorageModel.Sql;

    /// <summary>
    /// Creates a new SQL storage file set backed by the given streams.
    /// </summary>
    /// <param name="data">The storage stream for the data file (<c>.dat</c>).</param>
    /// <param name="journal">The storage stream for the journal file (<c>.log</c>).</param>
    /// <param name="backup">The storage stream for the backup file (<c>.bak</c>).</param>
    /// <param name="name">A name for this storage instance (e.g., database name).</param>
    /// <returns>A new <see cref="SqlStorage"/> ready for use.</returns>
    public static SqlStorage Create(StorageStream data, StorageStream journal, StorageStream backup, string name)
    {
        var storage = new SqlStorage(data, journal, backup);
        storage.InitializeNew((Name)name);
        return storage;
    }

    /// <summary>
    /// Creates a new SQL storage file set backed by arbitrary streams.
    /// </summary>
    /// <param name="data">The data stream (<c>.dat</c>).</param>
    /// <param name="journal">The journal stream (<c>.log</c>).</param>
    /// <param name="backup">The backup stream (<c>.bak</c>).</param>
    /// <param name="name">A name for this storage instance (e.g., database name).</param>
    /// <returns>A new <see cref="SqlStorage"/> ready for use.</returns>
    public static SqlStorage Create(System.IO.Stream data, System.IO.Stream journal, System.IO.Stream backup, string name)
    {
        return Create(new StorageStream(data), new StorageStream(journal), new StorageStream(backup), name);
    }

    /// <summary>
    /// Opens an existing SQL storage file set from the given streams.
    /// </summary>
    /// <param name="data">The storage stream for the data file (<c>.dat</c>).</param>
    /// <param name="journal">The storage stream for the journal file (<c>.log</c>).</param>
    /// <param name="backup">The storage stream for the backup file (<c>.bak</c>).</param>
    /// <returns>A <see cref="SqlStorage"/> loaded from the streams.</returns>
    public static SqlStorage Open(StorageStream data, StorageStream journal, StorageStream backup)
    {
        var storage = new SqlStorage(data, journal, backup);
        storage.OpenExisting();
        return storage;
    }

    /// <summary>
    /// Opens an existing SQL storage file set from arbitrary streams.
    /// </summary>
    /// <param name="data">The data stream (<c>.dat</c>).</param>
    /// <param name="journal">The journal stream (<c>.log</c>).</param>
    /// <param name="backup">The backup stream (<c>.bak</c>).</param>
    /// <returns>A <see cref="SqlStorage"/> loaded from the streams.</returns>
    public static SqlStorage Open(System.IO.Stream data, System.IO.Stream journal, System.IO.Stream backup)
    {
        return Open(new StorageStream(data), new StorageStream(journal), new StorageStream(backup));
    }

    /// <summary>
    /// Inserts a row into the storage.
    /// </summary>
    /// <param name="row">The serialized row bytes.</param>
    /// <returns>The page and slot location where the row was written.</returns>
    public (PageId PageId, int SlotIndex) InsertRow(ReadOnlySpan<byte> row)
    {
        return InsertRecord(row);
    }

    /// <summary>
    /// Reads a row from the specified page and slot.
    /// </summary>
    /// <param name="pageId">The page containing the row.</param>
    /// <param name="slotIndex">The slot index within the page.</param>
    /// <returns>A copy of the row bytes.</returns>
    public ReadOnlyMemory<byte> ReadRow(PageId pageId, int slotIndex)
    {
        return ReadRecord(pageId, slotIndex);
    }

    /// <summary>
    /// Updates a row at the specified page and slot.
    /// </summary>
    /// <param name="pageId">The page containing the row.</param>
    /// <param name="slotIndex">The slot index within the page.</param>
    /// <param name="row">The new row bytes.</param>
    public void UpdateRow(PageId pageId, int slotIndex, ReadOnlySpan<byte> row)
    {
        UpdateRecord(pageId, slotIndex, row);
    }

    /// <summary>
    /// Deletes a row at the specified page and slot.
    /// </summary>
    /// <param name="pageId">The page containing the row.</param>
    /// <param name="slotIndex">The slot index within the page.</param>
    public void DeleteRow(PageId pageId, int slotIndex)
    {
        DeleteRecord(pageId, slotIndex);
    }

    /// <summary>
    /// Flushes all pending changes to the underlying stream.
    /// </summary>
    public void FlushChanges()
    {
        Flush();
    }

    /// <summary>
    /// Gets the journal logger for transaction management.
    /// </summary>
    /// <returns>The <see cref="IJournalLogger"/> owned by this storage instance.</returns>
    internal IJournalLogger GetJournalLogger() => JournalLogger;
}
