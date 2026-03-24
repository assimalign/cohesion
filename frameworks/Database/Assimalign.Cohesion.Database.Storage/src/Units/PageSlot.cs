using System.Runtime.InteropServices;

namespace Assimalign.Cohesion.Database.Storage;

/// <summary>
/// Describes the location and size of a single variable-length record within a slotted page.
/// The slot directory at the end of a page contains an array of these entries, one per record.
/// </summary>
/// <remarks>
/// This is the universal record-location mechanism shared by all database models:
/// <list type="bullet">
///   <item><b>SQL:</b> Each slot points to a row (fixed + variable-length columns).</item>
///   <item><b>Document:</b> Each slot points to an encoded document (BSON, JSON, etc.).</item>
///   <item><b>KeyValuePair:</b> Each slot points to a key-value entry.</item>
///   <item><b>Graph:</b> Each slot points to a node record or edge record.</item>
/// </list>
/// A deleted slot is indicated by a <see cref="Length"/> of zero.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Size = 4, Pack = 1)]
public struct PageSlot
{
    /// <summary>
    /// The byte offset of the record data, measured from the start of the page.
    /// </summary>
    public ushort Offset;

    /// <summary>
    /// The length of the record data in bytes. A value of zero indicates a deleted slot.
    /// </summary>
    public ushort Length;

    /// <summary>
    /// Gets a value indicating whether this slot has been deleted.
    /// </summary>
    public readonly bool IsDeleted => Length == 0;

    /// <summary>
    /// Gets a value indicating whether this slot is empty (never been written to).
    /// </summary>
    public readonly bool IsEmpty => Offset == 0 && Length == 0;
}
