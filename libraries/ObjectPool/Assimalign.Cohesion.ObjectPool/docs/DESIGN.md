# Assimalign.Cohesion.ObjectPool Design

## Design Intent

The package keeps creation policy, return policy, and retention strategy separate so pooling behavior can stay generic and reusable across object types.

## Architecture

- ObjectPool<T> and ObjectPool<T, TArgs> define the rent and return contract.
- Factories create new instances while policies decide whether returned instances should be kept.
- DefaultObjectPool provides the standard retention behavior without forcing callers to build their own pool types.

## Retention limits

Both default pool variants validate `maximumRetained` in their common constructor, so direct
construction and every public `Create` overload reject negative values with
`ArgumentOutOfRangeException`. The count stays an `int`: it is a numeric range, not an identifier.

Retention uses one fast slot plus a queue whose capacity is `maximumRetained - 1`. A zero limit
disables both storage paths, including the fast slot, while preserving return-policy evaluation.
A limit of one permits only the fast slot. Renting from a zero-retention pool always creates an
instance through the configured factory.

Compared with `10.0.0-preview.1`, negative limits now fail during construction instead of being
accepted, and a zero limit no longer retains one instance. These are behavior changes; public
signatures and positive-limit retention behavior are unchanged.

## Layout Example

```text
Assimalign.Cohesion.ObjectPool/
  src/
    Assimalign.Cohesion.ObjectPool.csproj
    Extensions/
    Internal/
    Properties/
  tests/
  docs/
    OVERVIEW.md
    DESIGN.md
```

## Example 1: Rent and return pooled instances

```csharp
ObjectPool<StringBuilder> pool = ObjectPool<StringBuilder>.Create();
StringBuilder builder = pool.Rent();

try
{
    builder.Append("hello");
}
finally
{
    pool.Return(builder);
}
```

## Example 2: Provide a custom factory for the pool

```csharp
internal sealed class BufferFactory : ObjectPoolFactory<StringBuilder>
{
    public override StringBuilder Create() => new StringBuilder(256);
}

ObjectPool<StringBuilder> pool = ObjectPool<StringBuilder>.Create(new BufferFactory());
```
