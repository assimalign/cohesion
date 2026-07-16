# Assimalign.Cohesion.Database.Types — Overview

The shared scalar type system of the Cohesion Data Platform: type identity
(`DatabaseType`, `DatabaseTypeInfo`), explicit string collation (`Collation`), and
order-preserving binary key encodings (`DatabaseKeyWriter` / `DatabaseKeyReader`).
Anything that ends up inside an index key or a stored, comparable value goes through
this project so ordering is consistent across every database model.

## Scope

- **Type identity** — the `DatabaseType` scalar set (boolean, integers, floats,
  decimal, string, binary, date/time family, GUID, JSON) and `DatabaseTypeInfo`
  constraints (length/precision/scale).
- **Collation** — explicit, named, persisted-by-id string ordering rules:
  `Collation.Binary` (code-point order) and `Collation.Invariant` (culture-invariant
  linguistic order). No comparison in the platform ever depends on ambient culture.
- **Key encodings** — `DatabaseKeyWriter` builds self-describing composite keys whose
  unsigned byte-wise comparison equals component-by-component value comparison;
  `DatabaseKeyReader` decodes them back (round-trip). `Database.Indexing`'s `IndexKey`
  and every model's key convention consume these.
- **Boxed-value bridge** — `DatabaseValueCodec` maps boxed runtime values onto the
  same component encoding (dispatch by runtime type, read back boxed). The wire
  protocol's parameter and result-row payloads go through it on both the server and
  client sides.

## Dependencies

None — a leaf kernel project. Consumers: `Database.Indexing` (key composition),
`Database.Execution` (value typing), per-model catalogs and planners.

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
