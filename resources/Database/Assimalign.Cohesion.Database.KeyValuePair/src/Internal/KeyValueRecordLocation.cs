using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.KeyValuePair.Internal;

/// <summary>
/// Packs a record's physical location — page id and slot index — into one
/// <c>ulong</c> entry identity: the value the primary index maps keys to, and
/// the identity the version-store ledger keys on. (Key-grain locks key on the
/// key's hash instead — one key covers every version location it ever had.)
/// </summary>
internal static class KeyValueRecordLocation
{
    private const int slotBits = 16;

    internal static ulong Pack(PageId pageId, int slotIndex)
        => ((ulong)(long)pageId << slotBits) | (ushort)slotIndex;

    internal static (PageId PageId, int SlotIndex) Unpack(ulong packed)
        => ((PageId)(long)(packed >> slotBits), (int)(packed & 0xFFFF));
}
