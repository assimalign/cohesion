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
    public const string ErrorCode = "CK06101";

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
        string subject = string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.FormatExceptionDescribeIndexFormat, ErrorCode, indexName, objectId);

        if (foundVersion == 0)
        {
            return string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.IndexFormatExceptionDescribeIndex, subject, rootPageId, BTreeIndexManager.FormatVersion);
        }

        string remedy = foundVersion < BTreeIndexManager.FormatVersion
            ? ErrorMessages.FormatExceptionDescribeIndexFormat2
            : ErrorMessages.FormatExceptionDescribeIndexFormat3;

        return string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.IndexFormatExceptionDescribeIndex2, subject, foundVersion, BTreeIndexManager.FormatVersion, remedy);
    }
}
