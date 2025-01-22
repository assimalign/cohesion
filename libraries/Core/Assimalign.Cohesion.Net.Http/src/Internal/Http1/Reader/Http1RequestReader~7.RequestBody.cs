using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Http.Internal;

using Assimalign.Cohesion.Net.Transports;

internal class Http1RequestBodyReader : Http1RequestReader
{
    public override Http1RequestReader Next { get; } = default!;
    public override Task ReadAsync(Http1Context context, ITransportConnection connection)
    {

        var input = connection.Pipe.Input;
        var request = context.Request;
        var headers = request.Headers;

        //if (headers.TransferEncoding.HasValue && headers.TransferEncoding.Value == "chunked")
        //{
           
        //}
        //if (headers.ContentLength.HasValue)
        //{
        //    request.Body = new Http1RequestStream(new Http1ContentLengthMessageBody(input));
        //}
        //else
        //{
        //    //await next.Invoke(context);
        //}





        //var index = (long)0;
        //var length = default(int);
        //var result = await Input.ReadAsync();

        //if (TryReadLine(result, out var line, out var position))
        //{

        //    var t = Encoding.GetString(line);
        //    length = int.Parse(t);
        //    Input.AdvanceTo(position); 

        //}


        //while (true)
        //{
        //    var value = await Reader.ReadAsync();

        //    index += value.Buffer.Length;

        //    if (index >= length)
        //    {
        //        var temp = Encoding.ASCII.GetString(result.Buffer);
        //        break;
        //    }

        //    Reader.AdvanceTo(result.Buffer.End);

        //}

        return Task.CompletedTask;
    }
}