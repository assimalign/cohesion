using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Storage;

public abstract class Storage : IStorage
{
    public abstract StorageModel Model { get; }

    public StorageId Id => throw new NotImplementedException();

    public Name Name => throw new NotImplementedException();

    public void Dispose()
    {
        throw new NotImplementedException();
    }

    public ValueTask DisposeAsync()
    {
        throw new NotImplementedException();
    }

    public IStorageSegmentIterator GetIterator()
    {
        throw new NotImplementedException();
    }

    public IStorageSegmentIterator GetSegmentIterator()
    {
        throw new NotImplementedException();
    }

    public IStorageUnitIterator GetUnitIterator()
    {
        throw new NotImplementedException();
    }
}
