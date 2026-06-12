# Assimalign.Cohesion.Security Design

## Design Intent

Provides TLS as a *connection upgrade*: given an established, plaintext `IConnection`, produce a
new `IConnection` whose `Transport` is TLS-encrypted. This is the connection-filtering capability
the removed transport "pipeline" was reaching for — implemented correctly, so that the secured
connection actually transforms the byte stream the consumer reads and writes.

## Why an Upgrade, Not Middleware

The previous transport pipeline ran a callback once, after the connection's duplex pipe was
already constructed and wired to the socket. It could observe the connection but could not
*substitute* the stream, so it could never secure the bytes the consumer read — the documented
`SslStream` middleware example was non-functional. A connection upgrade avoids this by returning a
*new* `IConnection`: the inner (plaintext) transport is wrapped in an `SslStream`, and the secured
connection exposes the encrypted stream as its `Transport`. The consumer reads and writes
plaintext transparently; the encryption happens in between.

## How It Works

- `IConnection.UpgradeToTlsAsync(TlsServerOptions)` / `UpgradeToTlsAsync(TlsClientOptions)` are
  extension members that perform the handshake and return the secured connection.
- Internally: the inner `IConnection.Transport` (`IDuplexPipe`) is adapted to a `Stream`
  (`DuplexPipeStream`); an `SslStream` is constructed over it; the handshake runs
  (`AuthenticateAsServerAsync` / `AuthenticateAsClientAsync`); then the `SslStream` is exposed back
  as an `IDuplexPipe` via `PipeReader.Create` / `PipeWriter.Create`.
- The returned `TlsConnection` delegates `Id`, endpoints, `State`, `ConnectionClosed`, and `Abort`
  to the inner connection, reports `Capabilities` with `Security = Tls`, and exposes the encrypted
  pipe as its `Transport`.

## Lifecycle and Error Model

- Disposing the secured connection disposes the `SslStream`, then the inner connection.
- The handshake honors a configurable `HandshakeTimeout` (linked with the caller's cancellation
  token). On failure the `SslStream` is disposed and the exception (typically
  `AuthenticationException` from the platform TLS stack) propagates; the caller still owns the
  inner connection and is responsible for disposing it.

## Composition

TLS is applied at the composition root, after accepting or connecting:

```csharp
IConnection connection = await listener.AcceptAsync(cancellationToken);
IConnection secure = await connection.UpgradeToTlsAsync(serverOptions, cancellationToken);
// hand `secure` to the application protocol
```

Transports remain TLS-agnostic. Application protocols consume an `IConnection` without knowing
whether it is secured; they can inspect `Capabilities.Security` if they need to.

## AOT Posture

No reflection or runtime code generation. Uses the platform `SslStream` and `System.IO.Pipelines`.
NativeAOT compatible.

## Non-Goals

- Not a general-purpose connection/application middleware pipeline.
- Does not source certificates; certificates and authentication settings are supplied via options.
  Callers may use `Assimalign.Cohesion.Security.Cryptography` to obtain certificates.
- Does not implement cryptography; it delegates to the platform TLS stack via `SslStream`.

## Relationships

- **`Assimalign.Cohesion.Connections`** — the `IConnection` contract being upgraded.
- **`Assimalign.Cohesion.Security.Cryptography`** — certificate management that can supply
  certificates for the TLS options.
