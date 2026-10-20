namespace Assimalign.Cohesion.Database.Storage.Internal;


/// <summary>
/// The state one header slot of page 0 records (storage format 2).
/// </summary>
/// <param name="Generation">The header generation; the newest valid slot wins at open.</param>
/// <param name="LsnFloor">The journal's last LSN when the generation was written: the next LSN after a reopen exceeds it.</param>
/// <param name="SequenceFloor">The highest transaction sequence assigned when the generation was written.</param>
/// <param name="TotalPageCount">The data file's page count (informational; open recomputes it from the file length).</param>
/// <param name="FreePageCount">The free page count (informational; open rebuilds the free-space map from page headers).</param>
/// <param name="ModifiedAtUtcTicks">When the generation was written.</param>
/// <param name="AnchorCount">The number of logical transaction sequences in the checkpoint anchor.</param>
/// <param name="AnchorInlineCount">How many of them the slot holds itself; the rest are on the anchor chain.</param>
/// <param name="AnchorChainHead">The first anchor page, or zero when the slot holds the whole anchor.</param>
/// <param name="AnchorChainPageCount">The number of anchor pages on the chain.</param>
internal sealed record StorageHeaderSlot(
    long Generation,
    long LsnFloor,
    long SequenceFloor,
    long TotalPageCount,
    long FreePageCount,
    long ModifiedAtUtcTicks,
    int AnchorCount,
    int AnchorInlineCount,
    long AnchorChainHead,
    int AnchorChainPageCount);
