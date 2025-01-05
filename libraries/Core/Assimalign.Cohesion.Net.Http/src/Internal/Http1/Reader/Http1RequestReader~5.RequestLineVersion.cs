using System;
using System.Linq;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Http.Internal;

using Assimalign.Cohesion.Net.Transports;
using static Assimalign.Cohesion.Net.Http.Internal.HttpValues;

internal class Http1RequestLineVersionReader : Http1RequestReader
{
    public Http1RequestLineVersionReader()
    {
        Next = new Http1RequestHeadersReader();
    }

    public override Http1RequestReader Next { get; }

    public override async Task ReadAsync(Http1Context context, ITransportConnection connection)
    {
        var input = connection.Pipe.Input;
        var result = await input.ReadAsync();

        if (TryReadLine(result, out var line, out var position))
        {
            if (line.IsSingleSegment)
            {
                if (line.FirstSpan.SequenceEqual(Version1))
                {

                }
                else
                {
                    // Invalid Http Version
                    throw new Exception();
                }
            }
            else
            {
                // TODO: Throw exception. This should never happen, but just in-case there
                // are multiple segments that means the http request line is all jacked
            }

            input.AdvanceTo(position);
        }

        await Next.ReadAsync(context, connection);
    }
}
