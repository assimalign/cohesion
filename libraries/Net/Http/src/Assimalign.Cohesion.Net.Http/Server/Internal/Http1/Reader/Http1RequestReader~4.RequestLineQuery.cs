using System;
using System.Text;
using System.Buffers;
using System.Collections.Generic;
using System.Threading.Tasks;
using static Assimalign.Cohesion.Net.Http.Internal.HttpValues.Separators;

namespace Assimalign.Cohesion.Net.Http.Internal;

using Assimalign.Cohesion.Net.Transports;

internal class Http1RequestLineQueryReader : Http1RequestReader
{
    public Http1RequestLineQueryReader()
    {
        Next = new Http1RequestLineVersionReader();
    }

    public override Http1RequestReader Next { get; }

    public override async Task ReadAsync(Http1Context context, ITransportConnection connection)
    {
        var input = connection.Pipe.Input;
        var result = await input.ReadAsync();

        if (TryReadLine(result, out var line))
        {
            var remining = Parse(context, line);

            input.AdvanceTo(remining);
        }

        await Next.ReadAsync(context, connection);
    }

    private SequencePosition Parse(Http1Context context, ReadOnlySequence<byte> line)
    {

        var temp = Encoding.ASCII.GetString(line);

        var queryCollection = context.Request.Query;
        var reader = new SequenceReader<byte>(line);

        // If no Spce is found then just assume there is not query string to parse
        if (reader.TryReadTo(out ReadOnlySequence<byte> query, Space))
        {
            if (!query.IsSingleSegment)
            {
                throw new Exception();
            }

            var segment = query.FirstSpan;
            var key = new List<byte>();

            for (int i = 0; i < segment.Length; i++)
            {
                // Check for query separator
                if (segment[i] == (byte)'=')
                {
                    i++;
                    var value = new List<byte>();

                    for (; i < segment.Length; i++ )
                    {
                        if (segment[i] == (byte)'&')
                        {
                            break;
                        }
                        value.Add(segment[i]);
                    }

                    queryCollection.Add(
                        Encoding.ASCII.GetString(key.ToArray()),
                        Encoding.ASCII.GetString(value.ToArray()));

                    key.Clear();
                    value.Clear();
                }
                else
                {
                    key.Add(segment[i]);
                }
            }
        }

        return reader.Position;

    }

}
