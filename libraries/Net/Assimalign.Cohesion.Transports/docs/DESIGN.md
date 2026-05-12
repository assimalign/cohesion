# Assimalign.Cohesion.Transports Design

## Design Intent

The package splits networking into reusable layers: transports open connections, connections own pipes, and middleware runs around connection activity. That keeps protocol-specific work isolated from the general pipeline model.

## Architecture

- ClientTransport and ServerTransport define the shared connection lifecycle for protocols.
- TransportPipelineBuilder inserts middleware around ITransportConnection activity.
- Concrete TCP, UDP, and QUIC types reuse the shared contracts instead of each protocol inventing its own runtime shape.

## Layout Example

```text
Assimalign.Cohesion.Transports/
  src/
    Assimalign.Cohesion.Transports.csproj
    Abstractions/
    Delegates/
    Exceptions/
    Extensions/
    Internal/
    Properties/
    Transports/
  tests/
  docs/
    OVERVIEW.md
    DESIGN.md
```

## Example 1: Create a TCP client transport

```csharp
using var client = TcpClientTransport.Create(options =>
{
    options.EndPoint = new IPEndPoint(IPAddress.Loopback, 8080);
});

ITransportConnection connection = await client.ConnectAsync();
await connection.Pipe.WriteAsync(Encoding.UTF8.GetBytes("ping"));
```

## Example 2: Compose connection middleware

```csharp
ITransportPipeline pipeline = new TransportPipelineBuilder<ITransportConnection, ITransportConnectionContext>()
    .Use(async (connection, context, next) =>
    {
        context.AddItem("trace-id", Guid.NewGuid().ToString());
        await next(connection, context);
    })
    .Build();
```
