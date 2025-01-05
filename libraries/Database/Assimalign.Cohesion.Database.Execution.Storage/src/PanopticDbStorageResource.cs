using System;
using System.IO;
using System.IO.IsolatedStorage;
using System.IO.Compression;
using System.IO.MemoryMappedFiles;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Execution.Storage;


public class Cohesion.DatabaseStorageResource : ICohesion.DatabaseStorageResource
{

    private readonly FileStream resourceStream;


    public Cohesion.DatabaseStorageResource()
    {
       
        
    }

    public ICohesion.DatabaseStorageResourceHeader Header => throw new NotImplementedException();

    public ICohesion.DatabaseStorageIndexIterator CreateIndexIterator()
    {
        throw new NotImplementedException();
    }

    public ICohesion.DatabaseStorageSegmentIterator CreateSegmentIterator()
    {
        throw new NotImplementedException();
    }

    public void Dispose()
    {
        throw new NotImplementedException();
    }

    public ValueTask DisposeAsync()
    {
        throw new NotImplementedException();
    }

    public Task LoadAsync(Cohesion.DatabaseStorageContext context)
    {
        var indexHeader = CreateIndexIterator();

        indexHeader.
        
    }
}