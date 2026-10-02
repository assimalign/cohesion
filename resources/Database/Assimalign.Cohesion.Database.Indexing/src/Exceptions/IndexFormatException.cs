namespace Assimalign.Cohesion.Database.Indexing;

/// <summary>
/// Raised when an existing index is attached whose pages are not in the B-tree page
/// format this engine reads (<see cref="BTreeIndexManager.FormatVersion"/>): a tree
/// written by an engine before that format, by a newer one, or a registration that
/// does not point at a B-tree page at all.
/// </summary>
/// <remarks>
/// The index manager checks every existing tree's root page once, when
/// <see cref="BTreeIndexManager.Create"/> attaches it, and refuses the tree instead
/// of misreading it: there is no upgrade path for index pages (#1152). Model engines
/// raise the refusal from their database open, with this exception as the inner
/// exception and its coded message (<see cref="ErrorCode"/>) carried unchanged.
/// </remarks>
public sealed class IndexFormatException : IndexException
{
    /// <summary>
    /// The code that leads the message of every refusal: an index whose pages are in a
    /// page format this engine does not read.
    /// </summary>
    public const string ErrorCode = "COHDBI001";

    /// <summary>
    /// The code that leads the message of an <see cref="IndexException"/> raised when
    /// an attached tree reaches a page that is not a B-tree node of the current
    /// format: the index is damaged.
    /// </summary>
    public const string DamagedPageCode = "COHDBI002";

    /// <summary>
    /// Initializes a new <see cref="IndexFormatException"/>.
    /// </summary>
    /// <param name="indexName">The index whose pages were refused.</param>
    /// <param name="objectId">The object the index belongs to.</param>
    /// <param name="rootPageId">The index's registered root page.</param>
    /// <param name="foundVersion">The page format found, or 0 when the page is no B-tree page of any known format.</param>
    public IndexFormatException(string indexName, ulong objectId, long rootPageId, int foundVersion)
        : base(Describe(indexName, objectId, rootPageId, foundVersion))
    {
        IndexName = indexName;
        ObjectId = objectId;
        RootPageId = rootPageId;
        FoundVersion = foundVersion;
    }

    /// <summary>
    /// Gets the name of the index whose pages were refused.
    /// </summary>
    public string IndexName { get; }

    /// <summary>
    /// Gets the identity of the object the index belongs to.
    /// </summary>
    public ulong ObjectId { get; }

    /// <summary>
    /// Gets the registered root page of the index.
    /// </summary>
    public long RootPageId { get; }

    /// <summary>
    /// Gets the page format found on the root page, or 0 when the page is no B-tree
    /// page of any known format.
    /// </summary>
    public int FoundVersion { get; }

    /// <summary>
    /// Gets the only page format this engine reads.
    /// </summary>
    public int SupportedVersion => BTreeIndexManager.FormatVersion;

    private static string Describe(string indexName, ulong objectId, long rootPageId, int foundVersion)
    {
        string subject = $"{ErrorCode}: Index '{indexName}' on object {objectId}";

        if (foundVersion == 0)
        {
            return $"{subject} has a root page ({rootPageId}) that is not a B-tree page of any known format, " +
                $"and this engine supports only B-tree page format {BTreeIndexManager.FormatVersion}: the index is damaged.";
        }

        string remedy = foundVersion < BTreeIndexManager.FormatVersion
            ? "This engine does not upgrade index pages: export the data with the engine that wrote it, drop the database " +
              "and create it again with this engine (on-disk format upgrades are tracked by assimalign/cohesion#1152)."
            : "The index was written by a newer engine; open it with that engine.";

        return $"{subject} uses B-tree page format {foundVersion}, but this engine supports only format " +
            $"{BTreeIndexManager.FormatVersion}. {remedy}";
    }
}
