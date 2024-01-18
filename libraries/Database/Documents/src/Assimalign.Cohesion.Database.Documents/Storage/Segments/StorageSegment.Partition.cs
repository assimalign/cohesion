using Assimalign.PanopticDb.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.PanopticDb.Documents.Storage;

internal class PartitionStorageSegment : IStorageSegmentComposite
{

    public PartitionStorageSegment()
    {
        RandomAccess.
    }
    public SegmentId Id => throw new NotImplementedException();

    public SegmentLock Lock => throw new NotImplementedException();

    public Address Address => throw new NotImplementedException();

    public IStorageSegmentIterator GetSegmentIterator()
    {
        throw new NotImplementedException();
    }

    public IStorageUnitIterator GetUnitIterator()
    {
        throw new NotImplementedException();
    }

    public void ShiftDown(int units)
    {
        throw new NotImplementedException();
    }

    public void ShiftUp(int units)
    {
        throw new NotImplementedException();
    }
}
