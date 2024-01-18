using System;
using System.IO;
using System.IO.IsolatedStorage;
using System.IO.Compression;
using System.IO.MemoryMappedFiles;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.PanopticDb.Execution.Storage;


public class PanopticDbStorageResource : IPanopticDbStorageResource
{

    private readonly FileStream resourceStream;


    public PanopticDbStorageResource()
    {
       
        
    }

    public IPanopticDbStorageResourceHeader Header => throw new NotImplementedException();

    public IPanopticDbStorageIndexIterator CreateIndexIterator()
    {
        throw new NotImplementedException();
    }

    public IPanopticDbStorageSegmentIterator CreateSegmentIterator()
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

    public Task LoadAsync(PanopticDbStorageContext context)
    {
        var indexHeader = CreateIndexIterator();

        indexHeader.
        
    }
}