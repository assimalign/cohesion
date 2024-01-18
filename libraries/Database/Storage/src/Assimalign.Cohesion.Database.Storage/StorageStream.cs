using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.PanopticDb.Storage;

public sealed class StorageStream : Stream
{
    private readonly Stream innerStream;

    internal StorageStream(Stream innerStream)
    {
        this.innerStream = innerStream;
    }


    public override bool CanRead => innerStream.CanRead;
    public override bool CanSeek => innerStream.CanSeek;
    public override bool CanWrite => innerStream.CanWrite;
    public override long Length => innerStream.Length;
    public override long Position { get => innerStream.Position; set => innerStream.Position = value; }

    public override void Flush()
    {
        throw new NotImplementedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        throw new NotImplementedException();
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotImplementedException();
    }

    public override void SetLength(long value)
    {
        throw new NotImplementedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotImplementedException();
    }



    public static StorageStream FromFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new Exception();
        }

        return new StorageStream(File.Open(path, FileMode.OpenOrCreate));
    }


    public static StorageStream FromInMemory()
    {
        return new StorageStream(new MemoryStream());
    }
}
