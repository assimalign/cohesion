using System;
using System.Buffers;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Http.Internal;

using Assimalign.Cohesion.Net.Transports;
using static Assimalign.Cohesion.Net.Http.Internal.HttpValues.Separators;

internal class Http1RequestLineMethodReader : Http1RequestReader
{
    public Http1RequestLineMethodReader()
    {
        Next = new Http1RequestLineTargetReader();
    }

    public override Http1RequestReader Next { get; } 

    public override async Task ReadAsync(Http1Context context, ITransportConnection connection)
    {
        try
        {
            var input = connection.Pipe.Input;
            var result = await input.ReadAsync();

            // If the result is completed then there is something wrong with the underlying connection.
            if (result.IsCompleted)
            {

            }

            if (TryReadLine(result, out var line))
            {
                var remaining = Parse(context, line);

                input.AdvanceTo(remaining);
            }

            await Next.ReadAsync(context, connection);
        }
        catch (Exception exception) when (exception is not Http1Exception)
        {
            throw Http1Exception.InvalidRequest(exception);
        }
    }

    private SequencePosition Parse(Http1Context context, ReadOnlySequence<byte> line)
    {
        var reader = new SequenceReader<byte>(line);

        if (reader.TryReadTo(out ReadOnlySequence<byte> method, Space))
        {
            context.Request.Method = Encoding.ASCII.GetString(method);

            return reader.Position;
        }
        else
        {
            // TODO: Throw Invalid Request Line (There should be a space in-between '{Method} HTTP/1.1')

            throw new Exception();
        }
    }
}
