using System.IO;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Content.Binary.Tests;

internal sealed class TrackingMemoryStream : MemoryStream
{
    internal TrackingMemoryStream(byte[] data) : base(data) { }
    internal int Disposals { get; private set; }
    internal int AsyncDisposals { get; private set; }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Disposals++;
        }
        base.Dispose(disposing);
    }
    public override ValueTask DisposeAsync()
    {
        AsyncDisposals++;
        return base.DisposeAsync();
    }
}
