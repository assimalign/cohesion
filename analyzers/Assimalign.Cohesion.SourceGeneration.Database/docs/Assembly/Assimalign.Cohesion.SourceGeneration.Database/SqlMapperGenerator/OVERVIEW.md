# SqlMapperGenerator

`SqlMapperGenerator` is the public Roslyn `IIncrementalGenerator` entry point. Its parameterless
constructor lets the compiler instantiate it; `Initialize` registers semantic schema discovery
and generated source output. The compiler owns its lifecycle.

It emits an entity-namespace `<Entity>Mapper` with a parameterless constructor and these members:

| Member | Purpose |
| --- | --- |
| `GetKey(entity)` | Extract declared identity without discovery |
| `Capture(entity)` | Own a detached snapshot of declared persistent values |
| `AreEqual(left, right)` | Detect changes according to the documented value semantics |
| `Read(source)` | Materialize an entity from schema-ordered values |
| `Write(entity, target)` | Fill schema-ordered values from direct entity member access |
| Nested `Snapshot` | Carry captured state into transactional writes |
| `Snapshot.<Member>` | Expose each captured declared member to adapters; binary getters copy |

Public entities receive public mappers; other accessible entities receive internal mappers. The
snapshot constructor is internal and reachable only through mapper capture from public consumers.
All generated public members include XML documentation. See [the design](../../../DESIGN.md) for
diagnostics, scalar representations and null/ownership semantics.
