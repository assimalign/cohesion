namespace Assimalign.Cohesion.Database.Indexing;

/// <summary>
/// Raised when an attached index reaches a page that is not a B-tree node of the page
/// format this engine reads (<see cref="BTreeIndexManager.FormatVersion"/>): the index
/// is damaged.
/// </summary>
/// <remarks>
/// A tree is attached only after its root page passed the format check
/// (<see cref="IndexFormatException"/>, <c>COHDBI001</c>), and a tree is written by one
/// engine from its root down, so a page below the root that fails the same check is
/// damage, not an older format: a torn or overwritten page, or a pointer to one.
/// PostgreSQL reports the same condition as index corruption (<c>nbtpage.c</c>
/// <c>_bt_checkpage</c>, <c>ERRCODE_INDEX_CORRUPTED</c>). The operation that reached the
/// page fails, and whatever it had already changed rolls back with the caller's storage
/// bracket. This package has no repair path: a model whose index is a secondary
/// structure can drop it and build it again from its records.
/// Model engines that expose index failures on the area's error surface keep the coded
/// message (<see cref="ErrorCode"/>) and this exception as the inner exception.
/// </remarks>
public sealed class IndexCorruptionException : IndexException
{
    /// <summary>
    /// The code that leads the message: an attached index reached a damaged page.
    /// </summary>
    public const string ErrorCode = "COHDBI002";

    /// <summary>
    /// Initializes a new <see cref="IndexCorruptionException"/>.
    /// </summary>
    /// <param name="indexName">The index that reached the damaged page.</param>
    /// <param name="pageId">The damaged page.</param>
    /// <param name="foundVersion">The page format the page claims, or 0 when it is no B-tree page of any known format.</param>
    public IndexCorruptionException(string indexName, long pageId, int foundVersion)
        : base(
            $"{ErrorCode}: Index '{indexName}' reached page {pageId}, which is not a B-tree node of page format " +
            $"{BTreeIndexManager.FormatVersion} (found format {foundVersion}); the index is damaged.")
    {
        IndexName = indexName;
        PageId = pageId;
        FoundVersion = foundVersion;
    }

    /// <summary>
    /// Gets the name of the index that reached the damaged page.
    /// </summary>
    public string IndexName { get; }

    /// <summary>
    /// Gets the damaged page.
    /// </summary>
    public long PageId { get; }

    /// <summary>
    /// Gets the page format the page claims, or 0 when it is no B-tree page of any
    /// known format.
    /// </summary>
    public int FoundVersion { get; }
}
