namespace Assimalign.Cohesion.Database.Storage;

/// <summary>
/// Identifies the purpose and layout of a page within a storage file.
/// </summary>
[System.Flags]
public enum PageType : byte
{
    /// <summary>
    /// A catalog (system metadata) page.
    /// </summary>
    Catalog = 0,

    /// <summary>
    /// A data page containing user records.
    /// </summary>
    Data = 1,

    /// <summary>
    /// An index page (B+Tree internal or leaf node).
    /// </summary>
    Index = 2,

    /// <summary>
    /// A partition metadata page.
    /// </summary>
    Partition = 3,

    /// <summary>
    /// An overflow continuation page for records larger than a single page.
    /// </summary>
    Overflow = 4,

    /// <summary>
    /// A page storing large objects (text, ntext, image, nvarchar(max), varchar(max),
    /// varbinary(max), and xml data).
    /// </summary>
    LargeObject = 5,

    /// <summary>
    /// The storage file header page (page 0).
    /// </summary>
    FileHeader = 6,

    /// <summary>
    /// A free space map (allocation bitmap) page.
    /// </summary>
    FreeSpaceMap = 7,
}
