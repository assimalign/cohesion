
using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Http.Internal;

using Assimalign.Cohesion.Net.Transports;

internal abstract partial class Http1RequestReader
{
    public abstract Http1RequestReader Next { get; }
    public abstract Task ReadAsync(Http1Context context, ITransportConnection connection);
    protected bool TryReadLine(ReadResult result, out ReadOnlySequence<byte> line)
    {
        line = default;

        var cr = result.Buffer.PositionOf((byte)'\r');
        var lf = result.Buffer.PositionOf((byte)'\n');

        if (cr is not null && lf is not null)
        {
            line = result.Buffer.Slice(0, cr.Value);

            return true;
        }

        return false;
    }
    protected bool TryReadLine(ReadResult result, out ReadOnlySequence<byte> line, out SequencePosition lineEnding)
    {
        line = default;
        lineEnding = default;

        var cr = result.Buffer.PositionOf((byte)'\r');
        var lf = result.Buffer.PositionOf((byte)'\n');

        if (cr is not null && lf is not null)
        {
            line = result.Buffer.Slice(0, cr.Value);
            lineEnding = result.Buffer.GetPosition(1, lf.Value);

            return true;
        }

        return false;
    }
    public static Http1RequestReader Create() => new Http1RequestHttp2PrefaceReader();
}