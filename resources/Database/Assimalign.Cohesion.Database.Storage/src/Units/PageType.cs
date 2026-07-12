namespace Assimalign.Cohesion.Database.Storage;

/// <summary>
/// Identifies the purpose and layout of a page within a storage file.
/// </summary>
/// <remarks>
/// The page type is persisted in the page header, so values are append-only and never
/// renumbered. <see cref="Free"/> is deliberately zero: a zero-initialized page reads
/// as free, and freed pages are re-stamped with this value so the free-space map can
/// be reconstructed by scanning page headers when a storage file is reopened.
/// </remarks>
public enum PageType : byte
{
    /// <summary>
    /// An unallocated (free) page available for reuse.
    /// </summary>
    Free = 0,

    /// <summary>
    /// A data page containing user records.
    /// </summary>
    Data = 1,

    /// <summary>
    /// An index page (B+Tree internal or leaf node).
    /// </summary>
    Index = 2,

    /// <summary>
    /// A catalog (system metadata) page.
    /// </summary>
    Catalog = 3,

    /// <summary>
    /// A partition metadata page.
    /// </summary>
    Partition = 4,

    /// <summary>
    /// An overflow continuation page for records larger than a single page.
    /// </summary>
    Overflow = 5,

    /// <summary>
    /// A page storing large objects (text, ntext, image, nvarchar(max), varchar(max),
    /// varbinary(max), and xml data).
    /// </summary>
    LargeObject = 6,

    /// <summary>
    /// The storage file header page (page 0).
    /// </summary>
    FileHeader = 7,

    /// <summary>
    /// A free space map (allocation bitmap) page.
    /// </summary>
    FreeSpaceMap = 8,
}
