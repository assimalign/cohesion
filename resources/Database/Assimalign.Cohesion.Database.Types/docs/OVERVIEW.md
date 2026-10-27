# Assimalign.Cohesion.Database.Types — Overview

The shared scalar type system of the Cohesion Data Platform: type identity
(`DatabaseType`, `DatabaseTypeInfo`), explicit string collation (`Collation`), and
order-preserving binary key encodings (`DatabaseKeyWriter` / `DatabaseKeyReader`).
Anything that ends up inside an index key or a stored, comparable value goes through
this project so ordering is consistent across every database model.

## Scope

- **Diagnostics** — `Diagnostic`, `DiagnosticSeverity` and `DiagnosticLocation` are shared
  by language parsers and model-neutral execution results. Their names and members are unchanged
  after the move from Language (#1577, 2026-10-16).

- **Type identity** — the `DatabaseType` scalar set (boolean, integers, floats,
  decimal, fixed-length character, string, binary, date/time family, GUID, JSON) and
  `DatabaseTypeInfo` constraints (length/precision/scale). Character retains a distinct
  catalog/wire metadata identity; SQL stores padded strings and trims their index keys.
- **Collation** — explicit, named, persisted-by-id string ordering rules:
  `Collation.Binary` (code-point order), `CaseInsensitive` (Unicode simple case fold),
  and `CaseAccentInsensitive` (canonical decomposition, mark removal, case fold).
  Their Unicode 17.0 transforms are pinned and independent of runtime globalization.
  Legacy `Collation.Invariant` retains linguistic scan comparison but is explicitly
  **not index-backed** and rejects invariant-globalization operation.
- **Key encodings** — `DatabaseKeyWriter` builds self-describing composite keys whose
  unsigned byte-wise comparison equals component-by-component value comparison;
  `DatabaseKeyReader` decodes them back (folding collations return canonical text;
  original spelling stays in value storage) and can skip a component without
  materializing its value. `Database.Indexing`'s `IndexKey`
  and every model's key convention consume these.
- **Boxed-value bridge** — `DatabaseValueCodec` maps boxed runtime values onto the
  same component encoding (dispatch by runtime type, read back boxed). The wire
  protocol's parameter and result-row payloads go through it on both the server and
  client sides.

## Dependencies

None — a leaf kernel project. Consumers: `Database.Indexing` (key composition),
`Database.Execution` (value typing and diagnostics), `Database.Language` (diagnostic production),
per-model catalogs and planners.

## Usage

```csharp
var writer = new DatabaseKeyWriter();
writer.AppendInt64(customerId)
      .AppendString(region, Collation.Binary)
      .AppendDate(orderDate);

byte[] key = writer.ToArray(); // byte-comparable composite key

var reader = new DatabaseKeyReader(key);
long id = reader.ReadInt64();
```

See [DESIGN.md](DESIGN.md) for the encoding rules and the decisions behind them.

## Error and construction conventions (#1470)

Exception messages and format templates are generated from `src/Properties/ErrorMessages.resx`;
format arguments use the current culture. Cancellation and abort templates used during normal
cleanup are cached before reuse. Error identities remain separate from message text where a
structured diagnostic carrier exists. Standard .NET guards retain their argument names and exception types.

## Debugger inspection convention

Type-system debugger views filter private encoding workspaces. DatabaseKeyWriter exposes its existing Length field-backed getter. The ref-struct DatabaseKeyReader view snapshots only the public IsAtEnd Boolean, retaining no reader/span. Diagnostic keeps its preexisting debugger display exactly; types containing only public/protected data receive no proxy.

Concrete eligible types carry their own concise display and private nested DebugView at the end of the type; an effective inherited display/proxy pair is reused. Projections are public read-only members of that private view and never reveal private/internal data. Empty views use the constant type name. Debugger attributes and nested infrastructure add no public API, instance fields to existing runtime types, storage/wire format change or successful-path work.
