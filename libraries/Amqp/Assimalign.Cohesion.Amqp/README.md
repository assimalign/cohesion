# Assimalign.Cohesion.Amqp

The AMQP 1.0 model: connection, session, link and delivery contracts, the message format, delivery
states and outcomes, termini, SASL mechanisms, and the transaction controller and coordinator.
`Assimalign.Cohesion.Amqp.Connections` implements the contracts over Cohesion carrier connections;
code that sends, receives or settles programs against this package.

```csharp
using Assimalign.Cohesion.Amqp;

IAmqpSession session = await connection.BeginSessionAsync(cancellationToken: cancellationToken);
IAmqpSender sender = await session.AttachSenderAsync(new AmqpSenderOptions
{
    Target = new AmqpTarget { Address = "orders" }
}, cancellationToken);

AmqpDeliveryState? outcome = await sender.SendAsync(new AmqpMessage
{
    Body = new AmqpValueBody("order 42")
}, cancellationToken: cancellationToken);
```

See [docs/OVERVIEW.md](docs/OVERVIEW.md) for the key types and usage, and
[docs/DESIGN.md](docs/DESIGN.md) for the design record.
