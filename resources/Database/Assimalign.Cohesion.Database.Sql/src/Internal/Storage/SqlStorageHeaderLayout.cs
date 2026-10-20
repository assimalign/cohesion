using Assimalign.Cohesion.Database.Storage.Units;

namespace Assimalign.Cohesion.Database.Sql.Internal;

// Local storage-format constants: StorageHeaderPage owns these offsets internally in
// Database.Storage. Keep the probe's format knowledge here without widening that boundary.
internal static class SqlStorageHeaderLayout
{
    internal const int Slot0Offset = 512;
    internal const int SlotSize = 3584;
    internal const int Slot1Offset = Page.Size - SlotSize;
    internal const int GenerationOffset = Slot0Offset + sizeof(long);
    internal const int LsnFloorOffset = GenerationOffset + sizeof(long);
    internal const int SequenceFloorOffset = LsnFloorOffset + sizeof(long);
    internal const int TotalPagesOffset = SequenceFloorOffset + sizeof(long);
}
