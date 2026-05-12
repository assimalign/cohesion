using System;

namespace Assimalign.Cohesion.Database.Storage;

/// <summary>
/// Represents the smallest addressable data entity within a storage file.
/// A storage unit is a variable-length record stored in a slot within a page.
/// </summary>
/// <remarks>
/// At the storage layer, a unit is model-agnostic: it is simply a byte sequence
/// located by its page and slot index. The database models assign semantic meaning:
/// <list type="bullet">
///   <item><b>SQL:</b> A row (tuple) of column values.</item>
///   <item><b>Document:</b> An encoded document (BSON, JSON, etc.).</item>
///   <item><b>KeyValuePair:</b> A key-value entry.</item>
///   <item><b>Graph:</b> A node record or edge record.</item>
/// </list>
/// </remarks>
public interface IStorageUnit
{
    /// <summary>
    /// Gets the identifier of the page containing this unit.
    /// </summary>
    PageId PageId { get; }

    /// <summary>
    /// Gets the zero-based index of the slot within the page.
    /// </summary>
    int SlotIndex { get; }

    /// <summary>
    /// Gets the raw byte data of this storage unit.
    /// </summary>
    ReadOnlyMemory<byte> Data { get; }
}
