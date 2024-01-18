using Assimalign.Cohesion.Net.Transports;
using System.Buffers;
using System.Net;
using System.Net.Security;
using System.Text;

namespace Assimalign.Cohesion.Net.Transport.Tests;


public class TcpTransportTests
{


    [Fact]
    public async Task RequestResponseTest()
    {
        var thread = new Thread(async () =>
        {
            var transport = new TcpServerTransport(new());
            var connection = await transport.AcceptOrListenAsync();
            var result = await connection.Pipe.ReadAsync();
            var buffer = result.Buffer.ToArray();

            Assert.Equal("Client -> Server: Hello", Encoding.UTF8.GetString(buffer, 0, buffer.Length));

            var message = Encoding.UTF8.GetBytes("Server -> Client: Hello");
            var memory = new ReadOnlyMemory<byte>(message);

            await connection.Pipe.WriteAsync(memory);
        });

        thread.Start();

        var client = new TcpClientTransport(new());
        var connection = await client.ConnectAsync();
        

        var message = Encoding.UTF8.GetBytes("Client -> Server: Hello");
        var memory = new ReadOnlyMemory<byte>(message);

        await connection.Pipe.WriteAsync(memory);

        var result = await connection.Pipe.ReadAsync();
        var buffer = result.Buffer.ToArray();

        Assert.Equal("Server -> Client: Hello", Encoding.UTF8.GetString(buffer, 0, buffer.Length));
    }
}