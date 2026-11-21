# Assimalign.Cohesion.Amqp.Connections

## Summary

The AMQP 1.0 engine for Cohesion. `AmqpConnectionListener` and `AmqpConnectionFactory` establish
AMQP connections over carrier connections produced by the `Assimalign.Cohesion.Connections`
contracts, and return each one open: protocol headers exchanged, SASL done when configured, and
both `open` frames exchanged. The connections they return implement the root package's
`IAmqpConnection`, and the engine behind them runs the connection, session and link state machines,
checks every frame against the specification, and encodes the Part 1 type system and the Part 3
message format.

Carriers are chosen by capability, a reliable and ordered byte stream, never by protocol identity:
TCP, Unix domain sockets, named pipes, QUIC, TLS over any of them, or the in-memory driver.
Single-stream and multiplexed carriers are both supported.

## Dependencies

- `Assimalign.Cohesion.Amqp`: the model the engine implements
- `Assimalign.Cohesion.Connections`: the carrier contracts, listener and factory bases, and the
  layered listener that runs establishment
- `Assimalign.Cohesion.Core`

## Key Types

| Type | Role |
|---|---|
| `AmqpConnectionListener` (`IAmqpConnectionListener`) | Accepts AMQP connections over an `IDuplexConnectionListener` or `IMultiplexedConnectionListener` and returns each one open. It owns the carrier listener; a connection whose establishment fails or times out is dropped without stopping the listener. |
| `AmqpConnectionFactory` (`IAmqpConnectionFactory`) | Opens AMQP connections over an `IDuplexConnectionFactory` or `IMultiplexedConnectionFactory`, one new carrier per `ConnectAsync(EndPoint)` call. |
| `AmqpConnectionListenerOptions`, `AmqpConnectionFactoryOptions` | Container id, maximum frame size, channel maximum, idle and handshake timeouts, session defaults, the `open`'s capabilities and properties; the listener's SASL mechanisms and handshake bound, the factory's host name and SASL mechanism. |
| `AmqpMessageCodec` | Encodes and decodes `AmqpMessage` without a connection, and lets an intermediary read the bare message and replace the annotated sections around it unchanged. |

Everything an application does with a connection (sessions, links, deliveries, settlement,
transactions) is the root package's API; see its
[OVERVIEW.md](../../Assimalign.Cohesion.Amqp/docs/OVERVIEW.md).

## Usage

A server accepts open connections and the sessions and links peers begin on them:

```csharp
using System.Net;

using Assimalign.Cohesion.Amqp;
using Assimalign.Cohesion.Amqp.Connections;
using Assimalign.Cohesion.Connections.Tcp;

AmqpConnectionListenerOptions options = new() { ContainerId = "orders-broker" };
options.SaslMechanisms.Add(AmqpSaslPlainServerMechanism.Create(ValidateAsync));

// The AMQP listener takes ownership of the TCP listener.
await using AmqpConnectionListener listener = AmqpConnectionListener.Create(
    TcpConnectionListener.Create(tcp => tcp.EndPoint = new IPEndPoint(IPAddress.Any, 5672)),
    options);
await listener.BindAsync(cancellationToken);

await using IAmqpConnection connection = await listener.AcceptAsync(cancellationToken);
IAmqpSession session = await connection.AcceptSessionAsync(cancellationToken);
IAmqpLinkRequest request = await session.AcceptLinkAsync(cancellationToken);
```

A client connects and begins its own:

```csharp
AmqpConnectionFactory factory = AmqpConnectionFactory.Create(new TcpConnectionFactory(), new AmqpConnectionFactoryOptions
{
    SaslMechanism = AmqpSaslPlainClientMechanism.Create("user", password)
});

await using IAmqpConnection connection = await factory.ConnectAsync(new DnsEndPoint("broker", 5672), cancellationToken);
IAmqpSession session = await connection.BeginSessionAsync(cancellationToken: cancellationToken);
```

For TLS, compose the TLS layer on the carrier, below AMQP:
`AmqpConnectionListener.Create(tcpListener.UseTls(tlsServerOptions))` and
`AmqpConnectionFactory.Create(tcpFactory.UseTls(tlsClientOptions))`, with `UseTls` from
`Assimalign.Cohesion.Connections.Security`. The TLS facts are then on the AMQP connection's
features, and SASL EXTERNAL authenticates the client certificate.

Disposing the listener disposes the carrier listener and any connection still being established,
but not the connections it already returned: their owners dispose them. Disposing a connection
closes it with the `close` exchange; `Abort` ends it at once.

## Source Layout

```
src/
├── Abstractions/      IAmqpConnectionListener, IAmqpConnectionFactory
├── Internal/
│   ├── Carriers/      the multiplexed carrier adapters
│   ├── Encoding/      the Part 1 encoder and reader, the message format, terminus rules
│   ├── Engine/        the connection, session and link state machines, deliveries, the frame writer
│   ├── Establishment/ the handshake (headers, SASL, open), the listener's layer, settings
│   ├── Extensions/    carrier capability gating, exception helpers
│   ├── Frames/        frame layout and size checks
│   └── Performatives/ the Part 2 and Part 5 performatives
├── Properties/        AssemblyInfo.cs and ErrorMessages.resx
└── *.cs               AmqpConnectionListener, AmqpConnectionFactory, their options, AmqpMessageCodec
```

The design record, including the compliance maps, the interoperability tests against AMQPNetLite
(`tests/Interop/`) and the breaking changes from 10.0.0-preview.1, is [DESIGN.md](DESIGN.md).
