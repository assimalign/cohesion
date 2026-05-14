# `Assimalign.Cohesion.Logging.LoggerProviderBase`

Reusable abstract base class for `ILoggerProvider` implementations. Owns the disposal flag,
category validation, and the bridge between the strongly typed (`LoggerBase`) return type and
the `ILoggerProvider` interface.

## Members

| Member | Description |
| --- | --- |
| `abstract string Name { get; }` | Provider name; derived classes supply. |
| `bool IsDisposed { get; }` | True after `Dispose` has run. |
| `LoggerBase Create(string category)` | Validates the category, throws `ObjectDisposedException` after disposal, and calls `CreateCore`. Covariant return: the public method returns `LoggerBase` so callers holding a strongly typed reference avoid the interface cast. |
| `void Dispose()` | Idempotent; flips the disposed flag and calls `DisposeCore`. |
| `protected abstract LoggerBase CreateCore(string category)` | Derived classes build the actual logger here. |
| `protected virtual void DisposeCore()` | Implementation hook; defaults to no-op. |

`ILoggerProvider.Create(string)` is implemented as an explicit bridge that delegates to the
typed `Create(string)`. Callers holding the interface still get an `ILogger`; callers holding
the concrete provider type get a `LoggerBase`.

## Example

```csharp
public sealed class MyLoggerProvider : LoggerProviderBase
{
    public override string Name => "My";

    protected override LoggerBase CreateCore(string category)
        => new MyLogger(category, this);

    protected override void DisposeCore()
    {
        // release provider resources
    }
}
```
