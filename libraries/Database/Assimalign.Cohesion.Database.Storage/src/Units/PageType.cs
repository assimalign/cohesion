using System;

namespace Assimalign.Cohesion.Database.Storage;

[Flags]
public enum PageType : byte
{
    Catalog,
    Data,
    Index,
    Partition,
    Overflow,
    /// <summary>
    /// text, ntext, image, nvarchar(max), varchar(max), varbinary(max), and xml data
    /// </summary>
    LargeObject,
}
