
using System;
using System.Linq;
using System.Text;
using System.IO.Pipelines;
using System.Threading.Tasks;
using static System.Text.Encoding;

namespace Assimalign.Cohesion.Net.Http.Internal;

using static HttpValues.Separators;
using Assimalign.Cohesion.Net.Transports;


internal class Http1ResponseHeadersWriter : Http1ResponseWriter
{
    public Http1ResponseHeadersWriter()
    {
        Next = new Http1ResponseBodyWriter();
    }


    public override async Task WriteAsync(Http1Context context, ITransportConnection connection)
    {
        var writer = connection.Pipe.Output;
        var headers = context.Response.Headers;

        WriteHeaders(writer, headers);

        await writer.FlushAsync();

        //await Next?.WriteAsync(context, connection);
    }


    private void WriteHeaders(PipeWriter writer, IHttpHeaderCollection headers)
    {
        var i = 0;
        var memory = writer.GetMemory();

        foreach (var header in headers)
        {
            var key = ASCII.GetBytes(header.Key);
            var value = ASCII.GetBytes(header.Value);

            // Write Header Key
            for (int a = 0; a < key.Length; a++)
            {
                memory.Span[i] = key[a];

                if (i == memory.Length)
                {
                    Reset(ref i);
                }
                i++;
            }

            // Check if ': ' new two bytes equals or exceeds memory length
            if ((i + 2) >= memory.Length)
            {
                Reset(ref i);
            }

            // Write Header Separator
            memory.Span[i] = (byte)':';
            i++;
            memory.Span[i] = (byte)' ';
            i++;

            // Write Header Value
            for (int b = 0; b < value.Length; b++)
            {
                memory.Span[i] = value[b];

                if (i == memory.Length)
                {
                    Reset(ref i);
                }

                i++;
            }

            // Check if ' ' new two bytes equals or exceeds memory length
            if ((i + 3) >= memory.Length) // 3 is to 
            {
                Reset(ref i);
            }

            memory.Span[i] = NewLine[0];
            i++;
            memory.Span[i] = NewLine[1];
            i++;
        }
        if ((i + 2) >= memory.Length)
        {
            Reset(ref i);
        }

        memory.Span[i] = NewLine[0];
        i++;
        memory.Span[i] = NewLine[1];
        i++;

        writer.Advance(i);

        void Reset(ref int index)
        {
            writer.Advance(index);
            index = 0;
            memory = writer.GetMemory();
        }
    }
}
