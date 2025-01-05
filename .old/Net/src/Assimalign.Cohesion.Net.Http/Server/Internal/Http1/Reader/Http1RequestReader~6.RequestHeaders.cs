using System;
using System.Text;
using System.Buffers;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Http.Internal;

using Assimalign.Cohesion.Net.Transports;
using static Assimalign.Cohesion.Net.Http.Internal.HttpValues.Separators;

internal class Http1RequestHeadersReader : Http1RequestReader
{
    public Http1RequestHeadersReader()
    {
        Next = new Http1RequestBodyReader();
    }

    public override Http1RequestReader Next { get; }

    public override async Task ReadAsync(Http1Context context, ITransportConnection connection)
    {
        var input = connection.Pipe.Input;

        while (true)
        {
            var result = await input.ReadAsync();

            if (TryReadLine(result, out var line, out var position))
            {
                if (line.IsEmpty)
                {
                    input.AdvanceTo(position);
                    break;
                }

                Read(context, line);

                input.AdvanceTo(position);
            }
        }

        await Next.ReadAsync(context, connection);
    }

    private void Read(Http1Context context, ReadOnlySequence<byte> sequence)
    {
        var reader = new SequenceReader<byte>(sequence);

        if (reader.TryReadTo(out ReadOnlySequence<byte> headerKey, (byte)':'))
        {
            if (reader.TryPeek(out var next) && next == Space[0])
            {
                reader.Advance(1);
            }

            var key = Encoding.ASCII.GetString(headerKey);

            var value = Encoding.ASCII.GetString(reader.UnreadSpan);

            context.Request.Headers.Add(key, value);
        }
    }
}
