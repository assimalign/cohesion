# Assimalign.Cohesion.Database.Documents.Protocol design

This independently consumable contract library owns Documents framing payloads, validation and transferred values. Its direct contract references are Database.Protocol and Database.Types for scalar and diagnostic values. The engine and client use the same CLR DTO types and family singleton; there are no duplicate DTOs or type forwarders. Model request, planner, executor, catalog, storage and server state remain engine-owned.

The arrows show references:

```mermaid
flowchart LR
    Engine["Database.Documents"] --> Wire["Database.Documents.Protocol"]
    Client["Database.Documents.Client"] --> Wire
    Wire --> Shared["Database.Protocol / Types"]
```

Wire bytes, limits, malformed-payload behavior and exception text are preserved. Span-based codecs and explicit value handling remain trimming and NativeAOT compatible; no reflection serializer or dynamic discovery is introduced. The ProtocolMessageFamily is defined exactly once in this assembly. Existing codec tests live in this project's tests; engine/server/client integration tests retain their existing owners.
DocumentProtocolJson and DocumentProtocolWire live in src/Internal under the owning Protocol namespace. They preserve strict UTF-8 JSON parsing, complete-value checks and payload bounds. Their required ErrorMessages entries are owned and generated here; engine-only resources remain in Documents.
