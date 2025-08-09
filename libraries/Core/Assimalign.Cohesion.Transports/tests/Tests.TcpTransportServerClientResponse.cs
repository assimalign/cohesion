using Xunit;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Security;
using System.Buffers;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography;
using System.Text;
using System.Linq;

namespace Assimalign.Cohesion.Transports.Tests;

public class TcpTransportServerClientResponseTests
{

    [Fact]
    public void TestRequestResponse()
    {
        int i = 0;
        var server = GetServer(message =>
        {
            Assert.Equal("Client -> Server: Hello", message);
            i++;
        });
        var client = GetClient(message =>
        {
            Assert.Equal("Server -> Client: Hello", message);
            i++;
        });

        var timestamp = DateTime.Now;

        server.Start();
        client.Start();

        while (i != 2)
        {
            if (DateTime.Now > timestamp.AddSeconds(10))
            {
                Assert.Fail("The process ran too long and was unable to complete.");
            }
        }
    }
    private Thread GetClient(Action<string> callback)
    {
        return new Thread(async () =>
        {
            await using var transport = TcpClientTransport.Create(options =>
            {
                options.EndPoint = new IPEndPoint(IPAddress.Loopback, 8081);
                options.Use(async (connection, context, next) =>
                {
                    var pipe = context.Pipe;
                    var stream = pipe.GetStream();
                    var sslStream = new SslStream(stream, true);

                    await sslStream.AuthenticateAsClientAsync("localhost");

                    context.SetPipe(new TransportConnectionPipe(sslStream));

                    await next.Invoke(connection, context);
                });
            });


            await using var connection = await transport.ConnectAsync();
            var context = await connection.OpenAsync();
            var message = Encoding.UTF8.GetBytes("Client -> Server: Hello");
            var memory = new ReadOnlyMemory<byte>(message);

            await context.Pipe.WriteAsync(memory);

            var result = await context.Pipe.ReadAsync();
            var buffer = result.Buffer.ToArray();
            var data = Encoding.UTF8.GetString(buffer);

            callback.Invoke(data);
        });
    }
    private Thread GetServer(Action<string> callback)
    {
        return new Thread(async () =>
        {
            await using var transport = TcpServerTransport.Create(options =>
            {
                options.EndPoint = new IPEndPoint(IPAddress.Loopback, 8081);
                options.Use(async (connection, context, next) =>
                {
                    var pipe = context.Pipe;
                    var stream = pipe.GetStream();
                    var sslStream = new SslStream(stream);
                    var certificate = CertUtility.GetSelfSignedCertificate();

                    await sslStream.AuthenticateAsServerAsync(certificate);

                    context.SetPipe(new TransportConnectionPipe(sslStream));

                    await next.Invoke(connection, context);
                });
            });


            await using var connection = await transport.AcceptOrListenAsync();

            var context = await connection.OpenAsync();
            var result = await context.Pipe.ReadAsync();
            var buffer = result.Buffer.ToArray();
            var data = Encoding.UTF8.GetString(buffer);

            callback.Invoke(data);

            var message = Encoding.UTF8.GetBytes("Server -> Client: Hello");
            var memory = new ReadOnlyMemory<byte>(message);

            await context.Pipe.WriteAsync(memory);

            // Need to wait for the client to receive data before disposing of the underlying transport
            await Task.Delay(3000);
        });
    }
}
