using System;

namespace Assimalign.Cohesion.Database.Documents.Storage;

using Assimalign.Cohesion.Database.Storage;

/// <summary>
/// Provides a document-oriented storage engine built on the shared page-based storage layer.
/// Documents are stored as variable-length byte records in slotted pages.
/// </summary>
/// <remarks>
/// Each <see cref="DocumentStorage"/> instance manages three file assets: data (<c>.dat</c>),
/// journal (<c>.log</c>), and backup (<c>.bak</c>). Use the static factory methods
/// <see cref="Create(StorageStream, StorageStream, StorageStream, string)"/> and
/// <see cref="Open(StorageStream, StorageStream, StorageStream)"/> to create or open
/// storage instances.
/// </remarks>
/// <example>
/// <code>
/// // Create a new document storage backed by in-memory streams
/// using var data = StorageStream.FromInMemory();
/// using var journal = StorageStream.FromInMemory();
/// using var backup = StorageStream.FromInMemory();
/// using var storage = DocumentStorage.Create(data, journal, backup, "my-documents");
///
/// // Insert documents as UTF-8 encoded JSON
/// byte[] doc1 = System.Text.Encoding.UTF8.GetBytes("{\"name\":\"Alice\"}");
///
/// var (page1, slot1) = storage.InsertDocument(doc1);
///
/// // Read a document back
/// ReadOnlyMemory&lt;byte&gt; docData = storage.ReadDocument(page1, slot1);
/// string json = System.Text.Encoding.UTF8.GetString(docData.Span);
///
/// // Flush changes to the underlying streams
/// storage.FlushChanges();
/// </code>
/// </example>
public sealed class DocumentStorage : Assimalign.Cohesion.Database.Storage.Storage
{
    private DocumentStorage(StorageStream data, StorageStream journal, StorageStream backup)
        : base(data, journal, backup) { }

    /// <inheritdoc />
    public override StorageModel Model => StorageModel.Document;

    /// <summary>
    /// Creates a new document storage file set backed by the given streams.
    /// </summary>
    /// <param name="data">The storage stream for the data file (<c>.dat</c>).</param>
    /// <param name="journal">The storage stream for the journal file (<c>.log</c>).</param>
    /// <param name="backup">The storage stream for the backup file (<c>.bak</c>).</param>
    /// <param name="name">A name for this storage instance.</param>
    /// <returns>A new <see cref="DocumentStorage"/> ready for use.</returns>
    public static DocumentStorage Create(StorageStream data, StorageStream journal, StorageStream backup, string name)
    {
        var storage = new DocumentStorage(data, journal, backup);
        storage.InitializeNew((Name)name);
        return storage;
    }

    /// <summary>
    /// Creates a new document storage file set backed by arbitrary streams.
    /// </summary>
    /// <param name="data">The data stream (<c>.dat</c>).</param>
    /// <param name="journal">The journal stream (<c>.log</c>).</param>
    /// <param name="backup">The backup stream (<c>.bak</c>).</param>
    /// <param name="name">A name for this storage instance.</param>
    /// <returns>A new <see cref="DocumentStorage"/> ready for use.</returns>
    public static DocumentStorage Create(System.IO.Stream data, System.IO.Stream journal, System.IO.Stream backup, string name)
    {
        return Create(new StorageStream(data), new StorageStream(journal), new StorageStream(backup), name);
    }

    /// <summary>
    /// Opens an existing document storage file set from the given streams.
    /// </summary>
    /// <param name="data">The storage stream for the data file (<c>.dat</c>).</param>
    /// <param name="journal">The storage stream for the journal file (<c>.log</c>).</param>
    /// <param name="backup">The storage stream for the backup file (<c>.bak</c>).</param>
    /// <returns>A <see cref="DocumentStorage"/> loaded from the streams.</returns>
    public static DocumentStorage Open(StorageStream data, StorageStream journal, StorageStream backup)
    {
        var storage = new DocumentStorage(data, journal, backup);
        storage.OpenExisting();
        return storage;
    }

    /// <summary>
    /// Opens an existing document storage file set from arbitrary streams.
    /// </summary>
    /// <param name="data">The data stream (<c>.dat</c>).</param>
    /// <param name="journal">The journal stream (<c>.log</c>).</param>
    /// <param name="backup">The backup stream (<c>.bak</c>).</param>
    /// <returns>A <see cref="DocumentStorage"/> loaded from the streams.</returns>
    public static DocumentStorage Open(System.IO.Stream data, System.IO.Stream journal, System.IO.Stream backup)
    {
        return Open(new StorageStream(data), new StorageStream(journal), new StorageStream(backup));
    }

    /// <summary>
    /// Inserts a document into the storage.
    /// </summary>
    /// <param name="document">The raw document bytes to store.</param>
    /// <returns>The page and slot location where the document was written.</returns>
    public (PageId PageId, int SlotIndex) InsertDocument(ReadOnlySpan<byte> document)
    {
        return InsertRecord(document);
    }

    /// <summary>
    /// Reads a document from the specified page and slot.
    /// </summary>
    /// <param name="pageId">The page containing the document.</param>
    /// <param name="slotIndex">The slot index within the page.</param>
    /// <returns>A copy of the document bytes.</returns>
    public ReadOnlyMemory<byte> ReadDocument(PageId pageId, int slotIndex)
    {
        return ReadRecord(pageId, slotIndex);
    }

    /// <summary>
    /// Updates a document at the specified page and slot.
    /// </summary>
    /// <param name="pageId">The page containing the document.</param>
    /// <param name="slotIndex">The slot index within the page.</param>
    /// <param name="document">The new document bytes.</param>
    public void UpdateDocument(PageId pageId, int slotIndex, ReadOnlySpan<byte> document)
    {
        UpdateRecord(pageId, slotIndex, document);
    }

    /// <summary>
    /// Deletes a document at the specified page and slot.
    /// </summary>
    /// <param name="pageId">The page containing the document.</param>
    /// <param name="slotIndex">The slot index within the page.</param>
    public void DeleteDocument(PageId pageId, int slotIndex)
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
}
