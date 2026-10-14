using System;
using System.IO;

namespace Assimalign.Cohesion.Web.Diagnostics.Tests.TestObjects;

internal sealed class FailingAccessLogStream : MemoryStream
{
    public IOException? WriteFailure { get; set; }

    public IOException? FlushFailure { get; set; }

    public bool ClearFlushFailureAfterThrow { get; set; }

    public IOException? DisposeFailure { get; set; }

    public bool WasDisposed { get; private set; }

    public override void Write(byte[] buffer, int offset, int count)
    {
        if (WriteFailure is { } failure)
        {
            throw failure;
        }

        base.Write(buffer, offset, count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (WriteFailure is { } failure)
        {
            throw failure;
        }

        base.Write(buffer);
    }

    public override void Flush()
    {
        if (FlushFailure is { } failure)
        {
            if (ClearFlushFailureAfterThrow)
            {
                FlushFailure = null;
            }

            throw failure;
        }

        base.Flush();
    }

    protected override void Dispose(bool disposing)
    {
        WasDisposed = true;
        base.Dispose(disposing);
        if (disposing && DisposeFailure is { } failure)
        {
            throw failure;
        }
    }
}
