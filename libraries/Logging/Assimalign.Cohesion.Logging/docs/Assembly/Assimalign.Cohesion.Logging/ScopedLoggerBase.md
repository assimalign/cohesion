# `Assimalign.Cohesion.Logging.ScopedLoggerBase`

Reusable abstract base class for `IScopedLogger` implementations. Inherits the template-method
pattern from `LoggerBase` and adds idempotent disposal plus a `ParentId` field.

## Constructor

```csharp
protected ScopedLoggerBase(string category, LogId parentId);
```

Throws `ArgumentException` when `category` is null or empty.

## Members

| Member | Description |
| --- | --- |
| `LogId ParentId { get; }` | Id of the seed entry that opened the scope. |
| `bool IsDisposed { get; }` | True after `Dispose` has run. |
| `override bool IsEnabled(LogLevel level)` | Returns false when `IsDisposed`; otherwise delegates to `LoggerBase.IsEnabled`. |
| `void Dispose()` | Idempotent; flips the disposed flag and calls `DisposeCore`. |
| `protected virtual void DisposeCore()` | Implementation hook; defaults to no-op. |
| Inherited `WriteCore`, `BeginScopeCore` | Derived classes implement these from `LoggerBase`. |

## Behavior after disposal

- `IsEnabled(level)` returns false.
- `Log(entry)` short-circuits through `LoggerBase.Log` (silent drop) - no exception thrown.
- `BeginScope(entry)` still creates a child scope, but operations on that child will also
  short-circuit because the parent scope's disposal cascades through `IsEnabled`. Derived
  classes can override `BeginScopeCore` to throw `ObjectDisposedException` if stricter
  semantics are required.

## Example

```csharp
internal sealed class MyScopedLogger : ScopedLoggerBase
{
    private readonly MyProvider _provider;

    public MyScopedLogger(string category, LogId parentId, MyProvider provider)
        : base(category, parentId)
    {
        _provider = provider;
    }

    protected override void WriteCore(ILoggerEntry entry) => _provider.Emit(entry);
    protected override IScopedLogger BeginScopeCore(ILoggerEntry entry)
        => new MyScopedLogger(Category, entry.Id, _provider);
}
```
