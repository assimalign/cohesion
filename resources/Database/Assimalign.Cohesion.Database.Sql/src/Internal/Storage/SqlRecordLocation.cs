using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// Packs a record's physical location — page id and slot index — into one
/// <c>ulong</c> entry identity. The same packing keys both the lock manager's
/// row resources (<c>LockResource.Entry(objectId, entryId)</c>) and the version
/// store's ledger, so a row's lock and its version bookkeeping can never alias
/// different rows.
/// </summary>
internal static class SqlRecordLocation
{
    private const int slotBits = 16;

    internal static ulong Pack(PageId pageId, int slotIndex)
        => ((ulong)(long)pageId << slotBits) | (ushort)slotIndex;

    internal static (PageId PageId, int SlotIndex) Unpack(ulong packed)
        => ((PageId)(long)(packed >> slotBits), (int)(packed & 0xFFFF));
}
