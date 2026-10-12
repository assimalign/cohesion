# HTTP connections

The transport adapts connection listeners to HTTP/1.1, HTTP/2 and HTTP/3 exchanges.
Configure registrations and shared policies through `HttpConnectionListenerOptions`.

Applications may assign any status code from 100 through 599. An unregistered
final code such as 299 is sent with an empty HTTP/1.1 reason phrase; HTTP/2 and
HTTP/3 carry only its digits in `:status`. Codes from 100 through 199 belong to
the interim-response seam, and 101 remains the HTTP/1.1 upgrade path's status.

Cleartext HTTP/2 rejects `:scheme https` with 421 before dispatch. A trusted gateway
that terminates TLS may set `AllowAlternateSchemes = true` to preserve its forwarded
scheme. Connection security still reflects the backend connection.

Final responses include a cached IMF-fixdate `Date` by default. The timestamp is
available before dispatch and preserves an application override. Set
`AddDateHeader = false` behind a proxy that adds Date; 1xx responses omit it. The option
suppresses only the automatic header. StaticFiles keeps Last-Modified and date validators:
it uses a valid response Date, or one clock read when Date is absent, invalid, or repeated,
without adding or replacing the header.

See [DESIGN.md](DESIGN.md) for protocol behavior, limits and interception.
