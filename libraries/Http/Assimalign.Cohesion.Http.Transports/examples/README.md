# HTTP Transport Examples

This directory contains runnable localhost examples for the rebuilt HTTP transport layer:

- `Assimalign.Cohesion.Http.Transports.Examples.Http1`
- `Assimalign.Cohesion.Http.Transports.Examples.Http2`
- `Assimalign.Cohesion.Http.Transports.Examples.Http3`

Each example:

1. Starts a server backed by `Assimalign.Cohesion.Http.Transports`.
2. Uses `HttpClient` to send a real request to `localhost`.
3. Prints the negotiated protocol version and response payload.

## Run

```powershell
dotnet run --project .\examples\Assimalign.Cohesion.Http.Transports.Examples.Http1\Assimalign.Cohesion.Http.Transports.Examples.Http1.csproj
dotnet run --project .\examples\Assimalign.Cohesion.Http.Transports.Examples.Http2\Assimalign.Cohesion.Http.Transports.Examples.Http2.csproj
dotnet run --project .\examples\Assimalign.Cohesion.Http.Transports.Examples.Http3\Assimalign.Cohesion.Http.Transports.Examples.Http3.csproj
```

## Notes

- The HTTP/2 example uses prior-knowledge `h2c` over localhost and enables unencrypted HTTP/2 support for the sample process.
- The HTTP/3 example uses QUIC and requires a QUIC-capable platform and runtime.
- If the HTTP/3 sample fails during the QUIC/TLS handshake, verify local QUIC support and certificate handling on the host machine before debugging the HTTP/3 frame layer.
- All examples are single-request samples intended to demonstrate real end-to-end protocol flow on localhost.
