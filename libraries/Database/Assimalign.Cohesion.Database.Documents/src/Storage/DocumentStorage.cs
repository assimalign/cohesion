
using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Documents.Storage;

using Assimalign.Cohesion.Database.Storage;

/// <summary>
/// 16 bytes - Storage ID
/// 
/// 
/// </summary>
public sealed class DocumentStorage : IStorage
{

    private const int headerSize = 4096;


    private readonly Stream stream;


    internal DocumentStorage()
    {
        var handle = File.OpenHandle("");
        var buffer = new byte[0];
        var read = RandomAccess.Read(handle, buffer, 0);
        
    }


    public StorageId Id => throw new NotImplementedException();
    public Name Name => throw new NotImplementedException();
    public StorageModel Model => throw new NotImplementedException();

    

    public IStorageSegmentIterator GetSegmentIterator()
    {
        throw new NotImplementedException();
    }

    public IStorageUnitIterator GetUnitIterator()
    {
        throw new NotImplementedException();
    }




    private void Load(Stream stream)
    {
        // Storage Id Buffer
        var buffer = new byte[];


        if (stream is FileStream fileStream)
        {
            var handle = fileStream.SafeFileHandle;

            RandomAccess.ReadAsync()
        }

    }

    public void Dispose()
    {
        throw new NotImplementedException();
    }

    public ValueTask DisposeAsync()
    {
        throw new NotImplementedException();
    }



    public static DocumentStorage Open()
    {

    }
}
