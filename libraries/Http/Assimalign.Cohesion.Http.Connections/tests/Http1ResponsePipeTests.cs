using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>Complete buffered responses preserve ordering, prompt delivery and bounded flushes.</summary>
public class Http1ResponsePipeTests
{
    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http1 Response Pipe: Small pipelined responses should flush once each and remain ordered")]
    [InlineData(0, false)]
    [InlineData(13, false)]
    [InlineData(16000, false)]
    [InlineData(20000, false)]
    [InlineData(13, true)]
    public async Task SendAsync_OnPipelinedRequests_ShouldFlushOrderedCompleteResponses(int bodyLength, bool disableRate)
    {
        // Arrange
        const string request = "GET / HTTP/1.1\r\nHost: a\r\n\r\n";
        TestConnection transport = new(Encoding.ASCII.GetBytes(request + request
            + "GET / HTTP/1.1\r\nHost: a\r\nConnection: close\r\n\r\n"));
        ObservingPipeWriter output = new(transport.Output);
        transport.OutputOverride = output;
        HttpConnectionListenerOptions options = new();
        options.UseHttp1(new TestConnectionListener(transport), http1 =>
        {
            if (disableRate) { http1.Limits.MinResponseDataRate = null; }
        });
        await using HttpConnectionListener listener = new(options);
        await using IHttpConnection connection = await listener.AcceptAsync();
        IHttpConnectionContext server = await connection.OpenAsync();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        int count = 0;

        // Act
        await foreach (IHttpContext exchange in server.ReceiveAsync(timeout.Token))
        {
            await using (exchange)
            {
                string body = new((char)('a' + count), bodyLength);
                exchange.Response.Body = new MemoryStream(Encoding.ASCII.GetBytes(body));
                int before = output.FlushCount;
                await server.SendAsync(exchange, timeout.Token);

                // Assert — SendAsync makes this response available without waiting for the next
                // request, even though more complete heads are already buffered in the input.
                string response = Encoding.ASCII.GetString(await transport.ReadOutputAsync().WaitAsync(timeout.Token));
                response.ShouldStartWith("HTTP/1.1 200 ");
                response.ShouldEndWith("\r\n\r\n" + body);
                if (bodyLength <= 16000)
                {
                    (output.FlushCount - before).ShouldBe(1);
                }
                else
                {
                    (output.FlushCount - before).ShouldBeGreaterThan(1);
                }
                count++;
            }
        }
        count.ShouldBe(3);
    }
}
