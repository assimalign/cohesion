using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Transports.Tests;

public class UdpTransportTests
{

    public UdpTransportTests()
    {

    }

    /*
        * Test Packet
        +++++++++++++++++++++++++++++++++++++++++++++++
        +                          +                  +
        +   IP Address (16 bytes)  +  Port (4 bytes)  +
        +                          +                  +
        +++++++++++++++++++++++++++++++++++++++++++++++                          

        */

    [Fact]
    public async Task RequestResponseTest()
    {
        var transport = Transport.CreateUdpServer(options =>
        {
            options.AddMiddleware(builder =>
            {
                builder
                    .UseNext(async (context, next) =>
                    {
                        var pipe = context.Connection.Pipe;
                        var packet = await pipe.ReadAsync();

                        //var ip = new IPAddress(packet.Buffer..Take(4).ToArray());
                        //var port = BitConverter.ToUInt16(packet.Skip(16).Take(4).ToArray());
                        //var endPoint = new IPEndPoint(ip, port);

                        //context.SetRemoteEndPoint(endPoint);

                        await next(context);
                    })
                    .UseNext((context, next) =>
                    {

                        return Task.CompletedTask;
                    });
            });
        });

        var connection = transport.Initialize();
        var connectionReceiver = connection.Pipe.Input;
        var connectionSender = connection.Pipe.Output;

        

        while (true)
        {
            var result = connectionReceiver.ReadAsync();
        }


        // using var transport = new UdpServerTransport(new());

        // transport.Middleware.Enqueue(new UdpConnectionHandshake());

        // var connection = await transport.AcceptOrListenAsync();


        // connection.Pipe.Output.AsStream().Write(Encoding.UTF8.GetBytes("Hello World"));


        while (connection.State == ConnectionState.Running)
        {

        }
    }
}
