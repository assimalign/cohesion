# HTTP connections

The transport adapts connection listeners to HTTP/1.1, HTTP/2 and HTTP/3 exchanges.
Configure registrations and shared policies through `HttpConnectionListenerOptions`.

Cleartext HTTP/2 rejects `:scheme https` with 421 before dispatch. A trusted gateway
that terminates TLS may set `AllowAlternateSchemes = true` to preserve its forwarded
scheme. Connection security still reflects the backend connection.

Final responses include a cached IMF-fixdate `Date` by default. The timestamp is
available before dispatch and preserves an application override. Set
`AddDateHeader = false` behind a proxy that adds Date; 1xx responses omit it.

See [DESIGN.md](DESIGN.md) for protocol behavior, limits and interception.
