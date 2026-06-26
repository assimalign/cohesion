# Assimalign.Cohesion.Amqp.Transports

## Summary

The AMQP 1.0 transport layer for Cohesion: protocol-header negotiation, frame and message
codecs for the AMQP and SASL protocol phases, and server/client transports that establish AMQP
connections over carrier connections produced by the `Assimalign.Cohesion.Connections`
contracts. Carriers are selected by capability (a reliable, ordered byte stream), never by
protocol identity; both single-stream and multiplexed carriers are supported. Session, link,
and messaging semantics are out of scope — they belong to the sibling
`Assimalign.Cohesion.Amqp` library.

## Dependencies

- `Assimalign.Cohesion.Core`
- `Assimalign.Cohesion.Connections`

## Key Types

- `AmqpServerTransport` — accepts AMQP connections over an `IConnectionListener` or
  `IMultiplexedConnectionListener`; owns and disposes the listener.
- `AmqpClientTransport` — establishes AMQP connections to an `EndPoint` over an
  `IConnectionFactory` or `IMultiplexedConnectionFactory`.
- `IAmqpConnection` / `AmqpConnection` — an AMQP connection over a carrier; `Open()` /
  `OpenAsync()` yield the (cached) connection context.
- `IAmqpConnectionContext` / `AmqpConnectionContext` — the opened context; **is** an
  `IDuplexPipe`, with `AsStream()`, `NegotiateAsync` / `SwitchProtocolAsync`, and
  `SendAsync` / `ReceiveAsync` for frames.
- `AmqpTransportOptions` — initial protocol header, auto-negotiation toggle, max frame size.
- `AmqpProtocolHeader`, `AmqpProtocolId`, `AmqpFrame`, `AmqpFrameType` — the wire-level value
  types.
- `AmqpPerformative` family — `AmqpOpenPerformative`, `AmqpBeginPerformative`,
  `AmqpAttachPerformative`, `AmqpFlowPerformative`, `AmqpTransferPerformative`,
  `AmqpDispositionPerformative`, `AmqpDetachPerformative`, `AmqpEndPerformative`,
  `AmqpClosePerformative`, and the SASL performatives.
- `AmqpMessage`, `AmqpSource`, `AmqpTarget`, `AmqpError`, `AmqpSymbol`,
  `AmqpDescribedValue` — the AMQP 1.0 data model.
- `AmqpProtocolCodec`, `AmqpFrameCodec`, `AmqpMessageCodec` — static encode/decode facades.
- `AmqpProtocolException` — the area exception root.

## Source Layout

- `src/` — transports, connection/context guided bases, performative and message DTOs, value
  types, and the public codec facades.
- `src/Abstractions` — `IAmqpConnection`, `IAmqpConnectionContext`.
- `src/Exceptions` — `AmqpProtocolException`.
- `src/Extensions` — carrier capability gating (`ThrowIfNotAmqpCarrier`).
- `src/Internal` — the concrete carrier connections (single-stream, multiplexed), the
  connection context, and the `AmqpEncoding` wire toolbox.
