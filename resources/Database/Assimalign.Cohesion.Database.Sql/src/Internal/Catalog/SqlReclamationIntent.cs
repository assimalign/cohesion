using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Sql.Internal;

// Reference identity distinguishes a prepared intent from a later retirement intent for
// the same owner, even when the storage allocator reuses its catalog record location.
internal sealed class SqlReclamationIntent
{
    internal SqlReclamationIntent(PageId pageId, int slotIndex)
    {
        PageId = pageId;
        SlotIndex = slotIndex;
    }

    internal PageId PageId { get; }
    internal int SlotIndex { get; }
}
