# Assimalign.Cohesion.Amqp.Connections

The AMQP 1.0 engine over Cohesion carrier connections. `AmqpConnectionListener` and
`AmqpConnectionFactory` return connections that are open: protocol headers exchanged, SASL done
when configured, and both `open` frames exchanged, within a handshake timeout. Behind them the
engine runs the connection, session and link state machines of `Assimalign.Cohesion.Amqp`, and
checks every frame it sends or receives against the specification. Any reliable, ordered byte
stream can carry it: TCP, Unix domain sockets, named pipes, QUIC, TLS over any of them, or the
in-memory driver.

```csharp
using Assimalign.Cohesion.Amqp;
using Assimalign.Cohesion.Amqp.Connections;

// The listener takes ownership of any reliable, ordered carrier listener.
await using AmqpConnectionListener listener = AmqpConnectionListener.Create(carrierListener);
await listener.BindAsync(cancellationToken);

await using IAmqpConnection connection = await listener.AcceptAsync(cancellationToken);
IAmqpSession session = await connection.AcceptSessionAsync(cancellationToken);
IAmqpLinkRequest request = await session.AcceptLinkAsync(cancellationToken);
IAmqpReceiver receiver = await request.AcceptReceiverAsync(cancellationToken: cancellationToken);
```

A peer that fails or stalls its handshake loses only its own connection: the listener runs each
establishment on its own task, bounds how many run at once, and keeps accepting.

See [docs/OVERVIEW.md](docs/OVERVIEW.md) for the key types and usage, and
[docs/DESIGN.md](docs/DESIGN.md) for the design record, the compliance maps and the breaking
changes from 10.0.0-preview.1.
