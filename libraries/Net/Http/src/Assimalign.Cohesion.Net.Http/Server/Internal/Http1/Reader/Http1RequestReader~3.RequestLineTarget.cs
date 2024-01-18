
using System;
using System.Text;
using System.Buffers;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Http.Internal;

using Assimalign.Cohesion.Net.Transports;
using static Assimalign.Cohesion.Net.Http.Internal.HttpValues.Separators;


/// <summary>
/// Request Targets parser: https://httpwg.org/specs/rfc9112.html#rfc.section.3.2
/// </summary>
internal class Http1RequestLineTargetReader : Http1RequestReader
{
    public Http1RequestLineTargetReader()
    {
        Next = new Http1RequestLineQueryReader();
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
        var reader = new SequenceReader<byte>(line);

        if (reader.TryPeek(out var value))
        {
            // Let's check if the path is in origin-form
            // Origin form requires that the client send the requested path with a '/' as the beginning
            // https://httpwg.org/specs/rfc9112.html#rfc.section.3.2.1
            if (value == (byte)'/')
            {
                return ParseOriginForm(context, ref reader);
            }

            // Check for asterisk-form
            // https://httpwg.org/specs/rfc9112.html#rfc.section.3.2.4
            if (value == (byte)'*')
            {
                return ParseAstriskForm(context, ref reader);
            }

            // If the request method is CONNECT then we can assume the request target is in authority form
            // https://httpwg.org/specs/rfc9112.html#rfc.section.3.2.3
            if (context.Request.Method == HttpMethod.Connect)
            {
                return ParseAuthorityForm(context, ref reader);
            }
            
        }

        // If unable to peek something is wrong
        throw new Exception();
    }

    private SequencePosition ParseOriginForm(Http1Context context, ref SequenceReader<byte> reader)
    {
        if (reader.TryReadTo(out ReadOnlySequence<byte> path1, QuestionMark, true))
        {
            context.Request.Path = Encoding.ASCII.GetString(path1.FirstSpan);
            return reader.Position;
        }
        if (reader.TryReadTo(out ReadOnlySequence<byte> path2, Space, true))
        {
            context.Request.Path = Encoding.ASCII.GetString(path2.FirstSpan);
            return reader.Position;
        }

        throw new Exception();
    }

    private SequencePosition ParseAstriskForm(Http1Context context, ref SequenceReader<byte> reader)
    {
        if (reader.TryReadTo(out ReadOnlySequence<byte> path, QuestionMark, true))
        {

        }

        throw new NotImplementedException();
    }
    private SequencePosition ParseAuthorityForm(Http1Context context, ref SequenceReader<byte> reader)
    {
        if (reader.TryReadTo(out ReadOnlySequence<byte> path, QuestionMark, true))
        {

        }

        throw new NotImplementedException();
    }
}
