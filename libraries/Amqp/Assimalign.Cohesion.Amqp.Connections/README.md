# Assimalign.Cohesion.Amqp.Connections

AMQP 1.0 connections over Cohesion carrier connections. `AmqpConnectionListener` and
`AmqpConnectionFactory` establish each connection before handing it out: on a multiplexed carrier
they acquire its AMQP stream, then they exchange the protocol headers, within a handshake timeout.
The codecs read and write AMQP and SASL frames and the AMQP 1.0 message format. Any reliable,
ordered byte stream can carry it: TCP, Unix domain sockets, named pipes, QUIC, TLS over any of
them, or the in-memory driver.

```csharp
using Assimalign.Cohesion.Amqp.Connections;

// The listener takes ownership of any reliable, ordered carrier listener.
await using AmqpConnectionListener listener = AmqpConnectionListener.Create(carrierListener);
await listener.BindAsync(cancellationToken);

await using IAmqpConnection connection = await listener.AcceptAsync(cancellationToken);
await foreach (AmqpFrame frame in connection.ReceiveAsync(cancellationToken))
{
    // The protocol headers were exchanged before AcceptAsync returned.
}
```

A peer that fails or stalls its handshake loses only its own connection: the listener runs each
establishment on its own task, bounds how many run at once, and keeps accepting.

The performatives are wire DTOs. Connection, session and link behavior, delivery settlement and
SASL mechanisms belong to the layer above, the sibling `Assimalign.Cohesion.Amqp` library.

See [docs/OVERVIEW.md](docs/OVERVIEW.md) for the key types and usage, and
[docs/DESIGN.md](docs/DESIGN.md) for the design record and the breaking changes from
10.0.0-preview.1.
