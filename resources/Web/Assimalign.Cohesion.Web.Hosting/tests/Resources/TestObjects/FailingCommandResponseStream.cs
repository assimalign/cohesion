using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Web.Hosting.Resources;

namespace Assimalign.Cohesion.Web.Hosting.Tests.TestObjects;

internal sealed class FailingCommandResponseStream : MemoryStream
{
    private readonly ArgumentException _failure;

    internal FailingCommandResponseStream(ArgumentException failure)
    {
        _failure = failure;
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromException(_failure);
    }
}
