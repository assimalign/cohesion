# Assimalign.Cohesion.Http

The transport-independent HTTP contracts and protocol value objects used by
Cohesion clients, transports, and Web features. The package depends on Core;
feature-specific contracts live in the corresponding Http feature packages.
See [DESIGN](DESIGN.md) for protocol semantics and compatibility decisions.

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
