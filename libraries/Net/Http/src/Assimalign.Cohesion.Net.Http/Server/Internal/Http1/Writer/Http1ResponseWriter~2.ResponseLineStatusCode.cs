using System;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Http.Internal;

using static HttpValues.Separators;
using static HttpValues.StatusCodes;
using Assimalign.Cohesion.Net.Transports;

internal class Http1ResponseLineStatusCodeWriter : Http1ResponseWriter
{
    public Http1ResponseLineStatusCodeWriter()
    {
        Next = new Http1ResponseHeadersWriter();
    }
    public override Task WriteAsync(Http1Context context, ITransportConnection connection)
    {
        var writer = connection.Pipe.Output;
        var content = context.Response.StatusCode.Value switch
        {
            200 => Ok,
            201 => Created,
            202 => Accepted,
            203 => NonAuthoritativeInformation,
            204 => NoContent,
            205 => ResetContent,
            206 => PartialContent,

            400 => BadRequest,
            401 => Unauthorized,
            404 => NotFound,
            _ => throw new Exception()
        };

        var memory = writer.GetMemory(content.Length + NewLine.Length);

        int i = 0;

        // Write Status Code
        for (; i < content.Length; i++)
        {
            memory.Span[i] = content[i];
        }
        // Write New Line
        for (; i < (content.Length + NewLine.Length); i++)
        {
            memory.Span[i] = NewLine[i - content.Length];
        }

        writer.Advance(content.Length + NewLine.Length);

        return Next.WriteAsync(context, connection);
    }
}
