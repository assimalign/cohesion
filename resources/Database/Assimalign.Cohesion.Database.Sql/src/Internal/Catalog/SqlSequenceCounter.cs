using System;

using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Sql.Internal;

// One shared cursor for standalone and identity-owned sequence allocations.
internal sealed class SqlSequenceCounter
{
    public Int128 Next { get; set; }
    public int Remaining { get; set; }
    public (PageId PageId, int SlotIndex)? Location { get; set; }
    public long Reserved { get; set; }
    public bool IsCalled { get; set; }
}
