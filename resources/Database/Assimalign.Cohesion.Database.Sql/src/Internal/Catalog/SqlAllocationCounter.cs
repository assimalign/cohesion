using System;

using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Sql.Internal;

// The identity carrier retains T1's exact three-field layout. Sequence-only
// restart metadata belongs to the sealed derived carrier, with no extra object.
internal class SqlAllocationCounter
{
    public Int128 Next { get; set; }
    public int Remaining { get; set; }
    public (PageId PageId, int SlotIndex)? Location { get; set; }
}
