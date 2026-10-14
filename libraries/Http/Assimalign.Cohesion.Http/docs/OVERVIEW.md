# Assimalign.Cohesion.Http

The transport-independent HTTP contracts and protocol value objects used by
Cohesion clients, transports, and Web features. The package depends on Core;
feature-specific contracts live in the corresponding Http feature packages.
See [DESIGN](DESIGN.md) for protocol semantics and compatibility decisions.

## Structured fields and forwarding nodes

`StructuredFieldBareItem` factories validate values before retaining them.
Integers accept ±999,999,999,999,999. Decimals must remain within
±999,999,999,999.999 after rounding to three fractional digits with
round-half-to-even; `AsDecimal()` still returns the original input.
`FromDisplayString` accepts Unicode scalar values, including valid UTF-16
surrogate pairs, and rejects unpaired surrogates with `ArgumentException`.
Numeric range failures throw `ArgumentOutOfRangeException`.

`HttpForwardedNode.FromIPAddress(address, port)` accepts no port or a port
from 0 through 65535. Its parser enforces that range for IP nodes; `unknown`
and obfuscated names preserve numeric ports of one through five digits, such
as `unknown:99999`. Obfuscated ports remain supported. See DESIGN for the
earlier validation and stricter IP-node parsing changes from preview.1.

## Status codes

Construct `new HttpStatusCode(299)` or assign `HttpStatusCode status = 299` for
any integer from 100 through 599. `IsValid` checks that numeric range, not registry
membership. A code absent from the reason-phrase table has an empty
`ReasonPhrase`; `ToString()` renders `"299 "` for an HTTP/1.1 status line.
HTTP/2 and HTTP/3 send only the digits. Out-of-range values still throw.
This broadens preview.1's registered-only acceptance; see DESIGN for the
reserved-code and interim-response rules.

## Query values

Parse with `new HttpQuery("?tag=first&tag=&tag=last").Parse()`.
The collection has one key and its `query["tag"]` value contains three ordered
strings: `"first"`, `""`, and `"last"`. Enumerate that value or use its
integer indexer and `Count` to read all occurrences. Its `Value`, `ToString()`,
and typed scalar getters return the first occurrence (decision 31).

`HttpQueryCollection.Add` appends occurrences; assigning its key indexer
replaces all values for that key. `HttpQuery.Create(query)` encodes every
occurrence for a redirect or rewritten URL. Collection `Count` counts keys.
These behaviors replace preview.1's last-value parsing; see the compatibility
and caller audit in DESIGN before upgrading code that sends duplicate keys.

Transport integrations distinguish the `IConnection` identity/lifetime root from `IDuplexConnection` byte pipes and `IMultiplexedConnectionStream` streams. Core HTTP request/response and connection-info contracts do not acquire a transport dependency.

## Configured fields

Use `HttpFieldName name = "X-Trace"` and `HttpFieldValue value = "enabled"`
for validated configuration; use `TryCreate` when invalid text is expected. Names
compare without case sensitivity; values preserve case. Nullable string assignments
keep null as absence. A required consumer still refuses `default`.

`HttpAltService` accepts null/empty same-host values and rejects hosts containing
controls, spaces or tabs before an advertisement can be formatted.
