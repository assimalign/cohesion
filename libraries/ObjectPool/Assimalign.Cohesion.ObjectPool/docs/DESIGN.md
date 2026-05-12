# Assimalign.Cohesion.ObjectPool Design

## Design Intent

The package keeps creation policy, return policy, and retention strategy separate so pooling behavior can stay generic and reusable across object types.

## Architecture

- ObjectPool<T> and ObjectPool<T, TArgs> define the rent and return contract.
- Factories create new instances while policies decide whether returned instances should be kept.
- DefaultObjectPool provides the standard retention behavior without forcing callers to build their own pool types.

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
