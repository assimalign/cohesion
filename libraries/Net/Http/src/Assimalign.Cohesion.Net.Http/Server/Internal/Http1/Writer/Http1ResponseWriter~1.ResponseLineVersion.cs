using System;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Http.Internal;

using static HttpValues;
using Assimalign.Cohesion.Net.Transports;

internal class Http1ResponseLineVersionWriter : Http1ResponseWriter
{
    public Http1ResponseLineVersionWriter()
    {
        Next = new Http1ResponseLineStatusCodeWriter();
    }
    public override async Task WriteAsync(Http1Context context, ITransportConnection connection)
    {
        var writer = connection.Pipe.Output;
        var memory = writer.GetMemory();

        for (int i = 0; i < Version1.Length; i++)
        {
            memory.Span[i] = Version1[i];

            if (i + 1 == Version1.Length)
            {
                memory.Span[i + 1] = (byte)' ';
            }
        }
        
        writer.Advance(Version1.Length + 1);

        await Next.WriteAsync(context, connection);
    }


    private static byte[] GetNotFoundBytes()
    {
        var value = "404 Not Found";

        return Encoding.ASCII.GetBytes(value);
    }
}
