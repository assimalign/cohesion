# Assimalign.Cohesion.Amqp

## Summary

The AMQP 1.0 model: the contracts an application programs against and the values it exchanges.
It holds the connection, session and link contracts (`IAmqpConnection`, `IAmqpSession`,
`IAmqpSender`, `IAmqpReceiver`, `IAmqpDelivery`), the message format (`AmqpMessage` and its
sections), delivery states and outcomes, termini, the Part 1 values that have no CLR equivalent,
SASL mechanisms, and the transaction controller and coordinator of Part 4.

It implements no protocol. The engine that speaks AMQP over a carrier is
`Assimalign.Cohesion.Amqp.Connections`, whose `AmqpConnectionListener` and
`AmqpConnectionFactory` produce `IAmqpConnection` instances. A broker, a client, or a test double
programs against this package alone.

## Dependencies

- `Assimalign.Cohesion.Core`
- `Assimalign.Cohesion.Connections`: `IAmqpConnection` is an `IConnection`, and SASL mechanisms
  read the carrier connection's features (its TLS facts, for EXTERNAL).

`Assimalign.Cohesion.Amqp.Connections` references this package; this package never references it.

## Key Types

| Area | Types |
|---|---|
| Connection, session, link | `IAmqpConnection`, `IAmqpSession`, `IAmqpLink`, `IAmqpSender`, `IAmqpReceiver`, `IAmqpLinkRequest` (an incoming attach to accept or refuse), `IAmqpDelivery` |
| Options | `AmqpSessionOptions`, `AmqpLinkOptions` with `AmqpSenderOptions` and `AmqpReceiverOptions`, `AmqpDeliveryOptions` |
| Messages | `AmqpMessage`; its sections `AmqpHeader`, `AmqpProperties`, the annotation and property maps, and one body: `AmqpDataBody`, `AmqpSequenceBody` or `AmqpValueBody` |
| Delivery states | `AmqpDeliveryState`: `AmqpReceived`, `AmqpTransactionalState`, and the outcomes `AmqpAccepted`, `AmqpRejected`, `AmqpReleased`, `AmqpModified` and `AmqpDeclared` |
| Termini | `AmqpSource`, `AmqpTarget` and `AmqpCoordinator` (both `IAmqpTarget`), `AmqpTerminusDurability`, `AmqpTerminusExpiryPolicy`, `AmqpDistributionMode`, `AmqpLifetimePolicy`, `AmqpNodeProperties` |
| Values | `AmqpSymbol`, `AmqpDeliveryTag`, `AmqpDecimal32`, `AmqpDecimal64`, `AmqpDecimal128`, `AmqpDescribedValue` |
| SASL | `IAmqpSaslClientMechanism`, `IAmqpSaslServerMechanism` and their exchanges; PLAIN, ANONYMOUS and EXTERNAL on both sides; `IAmqpSaslConnectionInfo`, read as `connection.AmqpSasl` |
| Transactions | `AmqpTransactionController` (`IAmqpTransactionController`), `AmqpTransactionCoordinator` (`IAmqpTransactionCoordinator`) over an `IAmqpTransactionHandler`, `AmqpDeclare`, `AmqpDischarge`, `AmqpTransactionCapability` |
| Errors | `AmqpException` with `AmqpErrorCode`; `AmqpError` and the standard conditions in `AmqpErrorCondition` |

## Usage

A client sends a message and waits for its outcome. The connection comes from
`AmqpConnectionFactory` in `Assimalign.Cohesion.Amqp.Connections`:

```csharp
using Assimalign.Cohesion.Amqp;
using Assimalign.Cohesion.Amqp.Connections;

AmqpConnectionFactory factory = AmqpConnectionFactory.Create(tcpFactory, new AmqpConnectionFactoryOptions
{
    SaslMechanism = AmqpSaslPlainClientMechanism.Create("user", password)
});

await using IAmqpConnection connection = await factory.ConnectAsync(endPoint, cancellationToken);
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

A server accepts what a peer attaches and settles each delivery:

```csharp
await using IAmqpConnection connection = await listener.AcceptAsync(cancellationToken);
IAmqpSession session = await connection.AcceptSessionAsync(cancellationToken);
IAmqpLinkRequest request = await session.AcceptLinkAsync(cancellationToken);

if (request.Target is not AmqpTarget { Address: "orders" })
{
    await request.RejectAsync(new AmqpError(AmqpErrorCondition.NotFound), cancellationToken);
    return;
}

IAmqpReceiver receiver = await request.AcceptReceiverAsync(cancellationToken: cancellationToken);
IAmqpDelivery delivery = await receiver.ReceiveAsync(cancellationToken);
AmqpMessage message = delivery.GetMessage();
await delivery.AcceptAsync(cancellationToken);
```

Work done under a transaction is declared and discharged through a controller:

```csharp
await using AmqpTransactionController controller = await AmqpTransactionController.AttachAsync(session, cancellationToken: cancellationToken);
ReadOnlyMemory<byte> transactionId = await controller.DeclareAsync(cancellationToken);

await sender.SendAsync(message, new AmqpDeliveryOptions
{
    State = new AmqpTransactionalState(transactionId)
}, cancellationToken);

await controller.DischargeAsync(transactionId, cancellationToken: cancellationToken);
```

A broker runs the other end with `AmqpTransactionCoordinator.AcceptAsync(request, handler)` and its
own `IAmqpTransactionHandler`. [DESIGN.md](DESIGN.md) records what each side guarantees.

## Source Layout

```
src/
├── Abstractions/      connection, session, link, delivery, SASL and transaction contracts
├── Exceptions/        AmqpException and AmqpErrorCode
├── Extensions/        connection.AmqpSasl
├── Internal/          the built-in SASL exchanges
├── ValueObjects/      AmqpSymbol, AmqpDeliveryTag and the decimal types
├── Properties/        AssemblyInfo.cs and ErrorMessages.resx
└── *.cs               messages, delivery states, termini, options, SASL mechanisms, transactions
```
