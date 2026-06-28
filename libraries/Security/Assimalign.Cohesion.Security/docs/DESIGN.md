# Assimalign.Cohesion.Security Design

## Design Intent

Provides TLS as a *connection layer*: given an established, plaintext `IConnection`, produce a new
`IConnection` whose duplex pipe is TLS-encrypted. An `IConnection` *is* an `IDuplexPipe` — the
consumer reads bytes received from the peer off `Input` and writes bytes to send to `Output` — so
securing a connection means substituting those pipes with encrypted ones, not attaching a side
property. This is the connection-filtering capability the removed transport "pipeline" was reaching
for, implemented so the secured connection actually transforms the byte stream the consumer reads
and writes.

## Why a Layer, Not Middleware

The previous transport pipeline ran a callback once, after the connection's duplex pipe was already
constructed and wired to the socket. It could observe the connection but could not *substitute* the
pipe, so it could never secure the bytes the consumer read — the documented `SslStream` middleware
example was non-functional. A connection layer avoids this by returning a *new* `IConnection`: the
inner (plaintext) connection is wrapped in an `SslStream`, and the secured connection exposes the
encrypted stream as its `Input` / `Output`. The consumer reads and writes plaintext transparently;
the encryption happens in between.

## Public Surface

Two composition styles, both backed by the same decorator:

- **Layered (preferred for servers/clients).** `TlsConnectionLayer` implements `IConnectionLayer`.
  Compose it onto the accept/connect path with the extension members:
  - `IConnectionListener.UseTls(TlsServerOptions)` — secures every accepted connection (server auth).
  - `IConnectionFactory.UseTls(TlsClientOptions)` — secures every established connection (client auth).
- **One-shot.** `IConnection.UpgradeToTlsAsync(TlsServerOptions | TlsClientOptions, …)` performs the
  handshake on an individual, already-established connection and returns the secured one.

`TlsServerOptions` / `TlsClientOptions` wrap the BCL `SslServerAuthenticationOptions` /
`SslClientAuthenticationOptions` (exposed as `AuthenticationOptions`) plus a `HandshakeTimeout`.

## How It Works

- `TlsConnectionLayer.UpgradeAsync` (and the `UpgradeToTlsAsync` extensions) delegate to the internal
  `TlsConnection` decorator.
- Internally: the inner `IConnection` (itself an `IDuplexPipe`) is adapted to a `Stream` via
  `DuplexPipeStream`; an `SslStream` is constructed over it; the handshake runs
  (`AuthenticateAsServerAsync` / `AuthenticateAsClientAsync`); then the `SslStream` is exposed back
  as the connection's pipe via `PipeReader.Create` / `PipeWriter.Create`.
- The returned `TlsConnection` delegates `Id`, endpoints, `Direction`, `State`, `ConnectionClosed`,
  and `Abort` to the inner connection, reports `Capabilities` with `Security = Tls`, and exposes the
  encrypted pipe as its `Input` / `Output`.
- `TlsConnectionLayer.Describe` advertises `Security = Tls` so the capability is visible on the
  layered listener/factory before a connection is upgraded.

## Lifecycle and Error Model

- Disposing the secured connection disposes the `SslStream`, then the inner connection.
- The handshake honors a configurable `HandshakeTimeout` (linked with the caller's cancellation
  token). On failure the `SslStream` is disposed and the exception (typically
  `AuthenticationException` from the platform TLS stack) propagates; the caller still owns the inner
  connection and is responsible for disposing it.

## Composition

TLS is applied at the composition root, after accepting or connecting:

```csharp
// Server: secure every accepted connection.
IConnectionListener secured = listener.UseTls(serverOptions);
IConnection connection = await secured.AcceptAsync(cancellationToken);

// Or upgrade a single established connection.
IConnection secure = await connection.UpgradeToTlsAsync(serverOptions, cancellationToken);
// hand the secured connection to the application protocol
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

- **`Assimalign.Cohesion.Connections`** — the `IConnection` / `IConnectionLayer` contracts, the
  `UseTls` composition points (`IConnectionListener` / `IConnectionFactory`), and the
  `DuplexPipeStream` adapter.
- **`Assimalign.Cohesion.Security.Cryptography`** — certificate management that can supply
  certificates for the TLS options.
