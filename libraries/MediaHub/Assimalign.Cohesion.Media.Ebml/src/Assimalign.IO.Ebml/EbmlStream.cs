﻿using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.IO.Ebml;

/// <summary>
/// Represents a file that consists of one or more EBML Documents that are concatenated together
/// </summary>
public class EbmlStream : Stream
{
    private readonly Stream stream;

    public EbmlStream(Stream stream)
    {
        if (stream is null)
        {
            throw new ArgumentNullException(nameof(stream));
        }

        this.stream = stream;
    }

    public override bool CanRead => throw new NotImplementedException();
    public override bool CanSeek => throw new NotImplementedException();
    public override bool CanWrite => throw new NotImplementedException();
    public override long Length => throw new NotImplementedException();
    public override long Position { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

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
}
