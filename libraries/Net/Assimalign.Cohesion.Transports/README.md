# Transports




## Examples

Below are a set of quick start examples of how to use the built in transports.

### Creating a SSL/TLS Client and Server


```csharp

namespace YourClientApp;

public class Program 
{
	public static void Main() 
	{
	    using var transport = TcpClientTransport.Create(options =>
        {
            // Set the Endpoint to initialize the connection
            options.EndPoint = new IPEndPoint(IPAddress.Loopback, 8081);

            // Let's secure the connection by authenticating 
            options.AddMiddleware(builder =>
            {
                builder.UseNext(async (context, next) =>
                {
                    // let's grab the unsecure the stream when the connection is initialized
                    // and wrap it in an SSL/TLS stream.
                    var stream = context.Connection.Pipe.GetStream();
                    var sslStream = new SslStream(stream, true);

                    // Let's authenticate the client
                    // ! If authentication fails then you need to handle the exception thrown here
                    await sslStream.AuthenticateAsClientAsync("localhost");

                    // Now let's set the new connection pipe with 
                    // secure stream
                    context.SetPipe(new TransportConnectionPipe(sslStream));

                    await next.Invoke(context);
                });
            });
        });

        // Begin connection and await for server to respond
        var connection = await transport.ConnectAsync();

        var message = Encoding.UTF8.GetBytes("Client -> Server: Hello");
        var memory = new ReadOnlyMemory<byte>(message);

        await connection.Pipe.WriteAsync(memory);

        var result = await connection.Pipe.ReadAsync();
        var buffer = result.Buffer.ToArray();
        var data = Encoding.UTF8.GetString(buffer);

        callback.Invoke(data);
	}
}



```